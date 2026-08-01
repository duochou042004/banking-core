-- 0003 Journals, postings, and the database-side balancing defenses.

CREATE TABLE ledger.journal (
    journal_id                 uuid        PRIMARY KEY,
    ledger_id                  uuid        NOT NULL REFERENCES ledger.ledger_book (ledger_id),
    tenant_id                  uuid        NOT NULL,
    legal_entity_id            uuid        NOT NULL,
    ledger_sequence            bigint      NOT NULL,
    transaction_type           text        NOT NULL,
    schema_version             integer     NOT NULL,
    reason                     text        NOT NULL,
    external_reference         text        NULL,
    command_id                 uuid        NOT NULL,
    correlation_id             uuid        NOT NULL,
    causation_id               uuid        NULL,
    actor_id                   text        NOT NULL,
    actor_type                 text        NOT NULL,
    authorization_decision_id  uuid        NOT NULL,
    posted_at                  timestamptz NOT NULL,
    effective_at               timestamptz NOT NULL,
    booking_date               date        NOT NULL,
    value_date                 date        NOT NULL,
    business_date              date        NOT NULL,
    reverses_journal_id        uuid        NULL REFERENCES ledger.journal (journal_id),
    CONSTRAINT journal_sequence_unique UNIQUE (ledger_id, ledger_sequence),
    CONSTRAINT journal_sequence_positive CHECK (ledger_sequence >= 1),
    CONSTRAINT journal_schema_version_positive CHECK (schema_version >= 1),
    CONSTRAINT journal_actor_type_known CHECK (actor_type IN ('user', 'workload')),
    CONSTRAINT journal_transaction_type_shape CHECK (transaction_type ~ '^[a-z0-9][a-z0-9.-]{1,63}$'),
    CONSTRAINT journal_reason_length CHECK (char_length(reason) BETWEEN 1 AND 256),
    CONSTRAINT journal_external_reference_length CHECK (
        external_reference IS NULL OR char_length(external_reference) BETWEEN 1 AND 128),
    CONSTRAINT journal_not_self_reversing CHECK (reverses_journal_id IS DISTINCT FROM journal_id),
    CONSTRAINT journal_scope_fk FOREIGN KEY (ledger_id, tenant_id, legal_entity_id)
        REFERENCES ledger.ledger_book (ledger_id, tenant_id, legal_entity_id),
    -- Target of the posting foreign key, so a posting cannot attach to a journal in another ledger.
    CONSTRAINT journal_posting_key UNIQUE (journal_id, ledger_id, tenant_id)
);

-- A journal may be reversed at most once. Further correction uses a linked replacement journal
-- (docs/architecture/ledger.md, "Immutability and correction").
CREATE UNIQUE INDEX journal_single_reversal
    ON ledger.journal (reverses_journal_id)
    WHERE reverses_journal_id IS NOT NULL;

CREATE INDEX journal_by_command ON ledger.journal (tenant_id, command_id);
CREATE INDEX journal_by_correlation ON ledger.journal (tenant_id, correlation_id);
CREATE INDEX journal_by_effective_at ON ledger.journal (tenant_id, ledger_id, effective_at);

CREATE TABLE ledger.posting (
    posting_id    uuid          PRIMARY KEY,
    journal_id    uuid          NOT NULL,
    posting_order smallint      NOT NULL,
    account_id    uuid          NOT NULL,
    ledger_id     uuid          NOT NULL,
    tenant_id     uuid          NOT NULL,
    asset_id      uuid          NOT NULL,
    direction     text          NOT NULL,
    amount        numeric(38,0) NOT NULL,
    CONSTRAINT posting_order_unique UNIQUE (journal_id, posting_order),
    CONSTRAINT posting_order_positive CHECK (posting_order >= 1),
    CONSTRAINT posting_direction_known CHECK (direction IN ('debit', 'credit')),
    -- Zero postings are rejected and amounts are exact integers of atomic units.
    CONSTRAINT posting_amount_positive CHECK (amount > 0),
    CONSTRAINT posting_amount_integral CHECK (scale(amount) = 0),
    CONSTRAINT posting_journal_fk FOREIGN KEY (journal_id, ledger_id, tenant_id)
        REFERENCES ledger.journal (journal_id, ledger_id, tenant_id),
    CONSTRAINT posting_account_fk FOREIGN KEY (account_id, ledger_id, tenant_id, asset_id)
        REFERENCES ledger.ledger_account (account_id, ledger_id, tenant_id, asset_id)
);

CREATE INDEX posting_by_journal ON ledger.posting (journal_id);
CREATE INDEX posting_by_account ON ledger.posting (tenant_id, account_id);

-- Posted facts are insert-only for every role including the owner.
CREATE TRIGGER journal_is_insert_only
    BEFORE UPDATE OR DELETE ON ledger.journal
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();

CREATE TRIGGER posting_is_insert_only
    BEFORE UPDATE OR DELETE ON ledger.posting
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();

-- The independent balancing defense.
--
-- Evaluated at commit through deferred constraint triggers so the journal header and all of its
-- postings are visible together, whatever order the application inserted them in. Registering the
-- trigger on both relations closes the two ways an unbalanced fact could otherwise reach disk: a
-- journal inserted with no postings at all, and postings that do not sum per (ledger, asset).
-- Grouping by ledger as well as asset means an FX movement cannot be balanced by converted numbers
-- (evaluation AG-006).
CREATE OR REPLACE FUNCTION ledger.assert_journal_balanced() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_posting_count integer;
    v_unbalanced    integer;
BEGIN
    SELECT count(*) INTO v_posting_count
    FROM ledger.posting
    WHERE journal_id = NEW.journal_id;

    IF v_posting_count < 2 THEN
        RAISE EXCEPTION 'journal % has % posting(s); a journal requires at least two',
            NEW.journal_id, v_posting_count
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    SELECT count(*) INTO v_unbalanced
    FROM (
        SELECT 1
        FROM ledger.posting
        WHERE journal_id = NEW.journal_id
        GROUP BY ledger_id, asset_id
        HAVING coalesce(sum(amount) FILTER (WHERE direction = 'debit'), 0)
             <> coalesce(sum(amount) FILTER (WHERE direction = 'credit'), 0)
    ) AS unbalanced_groups;

    IF v_unbalanced > 0 THEN
        RAISE EXCEPTION
            'journal % is not balanced: debits must equal credits for every (ledger, asset) group',
            NEW.journal_id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER journal_must_balance
    AFTER INSERT ON ledger.journal
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION ledger.assert_journal_balanced();

CREATE CONSTRAINT TRIGGER posting_must_balance_journal
    AFTER INSERT ON ledger.posting
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION ledger.assert_journal_balanced();

-- A reversal must mirror exactly one posted journal that is not itself a reversal, and must stay
-- inside the same ledger and scope (evaluation AG-007).
CREATE OR REPLACE FUNCTION ledger.assert_reversal_shape() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_original ledger.journal%ROWTYPE;
BEGIN
    IF NEW.reverses_journal_id IS NULL THEN
        RETURN NULL;
    END IF;

    SELECT * INTO v_original FROM ledger.journal WHERE journal_id = NEW.reverses_journal_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'journal % reverses an unknown journal', NEW.journal_id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    IF v_original.reverses_journal_id IS NOT NULL THEN
        RAISE EXCEPTION 'journal % reverses a journal that is itself a reversal', NEW.journal_id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    IF v_original.ledger_id <> NEW.ledger_id OR v_original.tenant_id <> NEW.tenant_id THEN
        RAISE EXCEPTION 'journal % reverses a journal in a different ledger or tenant', NEW.journal_id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    IF NEW.ledger_sequence <= v_original.ledger_sequence THEN
        RAISE EXCEPTION 'a reversal must be sequenced after the journal it reverses'
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER journal_reversal_shape
    AFTER INSERT ON ledger.journal
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION ledger.assert_reversal_shape();
