-- 0002 Assets, ledgers, ledger accounts, aggregates, and accounting periods.

-- Assets are deployment-wide reference data, not tenant data, so they carry no isolation scope and
-- no row level security policy. Scale is immutable after use; a redenomination is a modeled
-- migration, not an update here (docs/architecture/ledger.md, "Value model").
CREATE TABLE ledger.asset (
    asset_id            uuid        PRIMARY KEY,
    code                text        NOT NULL,
    scale               smallint    NOT NULL,
    status              text        NOT NULL,
    external_standard   text        NULL,
    external_code       text        NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT asset_code_unique UNIQUE (code),
    CONSTRAINT asset_code_shape CHECK (code ~ '^[A-Z0-9][A-Z0-9._-]{1,31}$'),
    CONSTRAINT asset_scale_range CHECK (scale BETWEEN 0 AND 18),
    CONSTRAINT asset_status_known CHECK (status IN ('active', 'suspended', 'retired')),
    CONSTRAINT asset_external_pair CHECK (
        (external_standard IS NULL AND external_code IS NULL)
        OR (external_standard IS NOT NULL AND external_code IS NOT NULL))
);

CREATE TABLE ledger.ledger_book (
    ledger_id       uuid        PRIMARY KEY,
    tenant_id       uuid        NOT NULL,
    legal_entity_id uuid        NOT NULL,
    code            text        NOT NULL,
    status          text        NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ledger_book_code_unique UNIQUE (tenant_id, code),
    CONSTRAINT ledger_book_code_shape CHECK (code ~ '^[a-z0-9][a-z0-9.-]{1,63}$'),
    CONSTRAINT ledger_book_status_known CHECK (status IN ('open', 'closed')),
    -- Referenced by journals so that a journal cannot claim a scope its ledger does not have.
    CONSTRAINT ledger_book_scope_key UNIQUE (ledger_id, tenant_id, legal_entity_id)
);

CREATE TABLE ledger.ledger_account (
    account_id      uuid        PRIMARY KEY,
    ledger_id       uuid        NOT NULL REFERENCES ledger.ledger_book (ledger_id),
    tenant_id       uuid        NOT NULL,
    legal_entity_id uuid        NOT NULL,
    code            text        NOT NULL,
    asset_id        uuid        NOT NULL REFERENCES ledger.asset (asset_id),
    account_class   text        NOT NULL,
    normal_side     text        NOT NULL,
    status          text        NOT NULL,
    purpose         text        NOT NULL,
    balance_policy  text        NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ledger_account_code_unique UNIQUE (ledger_id, code),
    CONSTRAINT ledger_account_code_shape CHECK (code ~ '^[a-z0-9][a-z0-9.:-]{1,63}$'),
    CONSTRAINT ledger_account_class_known CHECK (
        account_class IN ('asset', 'liability', 'equity', 'income', 'expense')),
    CONSTRAINT ledger_account_side_known CHECK (normal_side IN ('debit', 'credit')),
    CONSTRAINT ledger_account_status_known CHECK (status IN ('open', 'frozen', 'closed')),
    CONSTRAINT ledger_account_policy_known CHECK (
        balance_policy IN ('posted-only-never-negative-v1', 'posted-only-unrestricted-v1')),
    -- Inherits the owning ledger's scope, so an account cannot be attached across tenants.
    CONSTRAINT ledger_account_scope_fk FOREIGN KEY (ledger_id, tenant_id, legal_entity_id)
        REFERENCES ledger.ledger_book (ledger_id, tenant_id, legal_entity_id),
    -- Target of the posting foreign key: a posting must agree with its account on ledger, tenant,
    -- and asset, so the database rejects cross-scope and wrong-asset references without any
    -- application check (docs/architecture/ledger.md, "Required database defenses").
    CONSTRAINT ledger_account_posting_key UNIQUE (account_id, ledger_id, tenant_id, asset_id)
);

CREATE INDEX ledger_account_by_ledger ON ledger.ledger_account (tenant_id, ledger_id);

-- Authoritative aggregates. Debit and credit totals are the primary facts; a signed balance is
-- calculated from them and the account's normal side, never stored
-- (docs/architecture/ledger.md, "Accounting model").
CREATE TABLE ledger.account_balance (
    account_id    uuid          PRIMARY KEY REFERENCES ledger.ledger_account (account_id),
    tenant_id     uuid          NOT NULL,
    ledger_id     uuid          NOT NULL,
    asset_id      uuid          NOT NULL,
    debit_total   numeric(38,0) NOT NULL DEFAULT 0,
    credit_total  numeric(38,0) NOT NULL DEFAULT 0,
    posting_count bigint        NOT NULL DEFAULT 0,
    version       bigint        NOT NULL DEFAULT 0,
    updated_at    timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT account_balance_debit_non_negative CHECK (debit_total >= 0),
    CONSTRAINT account_balance_credit_non_negative CHECK (credit_total >= 0),
    CONSTRAINT account_balance_scale CHECK (scale(debit_total) = 0 AND scale(credit_total) = 0),
    CONSTRAINT account_balance_count_non_negative CHECK (posting_count >= 0),
    CONSTRAINT account_balance_version_non_negative CHECK (version >= 0),
    CONSTRAINT account_balance_account_fk FOREIGN KEY (account_id, ledger_id, tenant_id, asset_id)
        REFERENCES ledger.ledger_account (account_id, ledger_id, tenant_id, asset_id)
);

-- Aggregates only ever move forward, and each committing journal advances the version by exactly
-- one. This makes "update a balance without postings" visible as a version anomaly and blocks the
-- silent repair pattern rejected by evaluation AG-002.
CREATE OR REPLACE FUNCTION ledger.assert_balance_progression() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.account_id <> OLD.account_id OR NEW.ledger_id <> OLD.ledger_id
        OR NEW.tenant_id <> OLD.tenant_id OR NEW.asset_id <> OLD.asset_id THEN
        RAISE EXCEPTION 'account aggregate identity is immutable'
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF NEW.debit_total < OLD.debit_total OR NEW.credit_total < OLD.credit_total THEN
        RAISE EXCEPTION 'account aggregates are monotonic; reduce a balance with a linked reversal'
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF NEW.posting_count < OLD.posting_count THEN
        RAISE EXCEPTION 'account posting count is monotonic'
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF NEW.version <> OLD.version + 1 THEN
        RAISE EXCEPTION 'account aggregate version must advance by exactly one per committing journal'
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER account_balance_progression
    BEFORE UPDATE ON ledger.account_balance
    FOR EACH ROW EXECUTE FUNCTION ledger.assert_balance_progression();

CREATE TRIGGER account_balance_no_delete
    BEFORE DELETE ON ledger.account_balance
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();

-- Gap-free per-ledger commit order. A PostgreSQL sequence would leave gaps on rollback, and the
-- ledger constitution requires sequence gaps to be explained; a counter row makes the sequence
-- dense and therefore trivially reconcilable. The cost is that concurrent journals in one ledger
-- serialize on this row, which is recorded as a known throughput limit of the slice.
CREATE TABLE ledger.ledger_sequence_state (
    ledger_id     uuid   PRIMARY KEY REFERENCES ledger.ledger_book (ledger_id),
    tenant_id     uuid   NOT NULL,
    next_sequence bigint NOT NULL DEFAULT 1,
    CONSTRAINT ledger_sequence_positive CHECK (next_sequence >= 1)
);

-- Closing a period prevents new effective dates in that period except through a separately
-- authorized adjustment process (docs/architecture/ledger.md, "Identity, order, and time";
-- evaluation AG-010). Periods within one ledger may not overlap.
CREATE TABLE ledger.accounting_period (
    period_id    uuid        PRIMARY KEY,
    ledger_id    uuid        NOT NULL REFERENCES ledger.ledger_book (ledger_id),
    tenant_id    uuid        NOT NULL,
    period_start date        NOT NULL,
    period_end   date        NOT NULL,
    status       text        NOT NULL,
    closed_at    timestamptz NULL,
    closed_by    text        NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT accounting_period_range CHECK (period_end >= period_start),
    CONSTRAINT accounting_period_status_known CHECK (status IN ('open', 'closed')),
    CONSTRAINT accounting_period_closed_evidence CHECK (
        (status = 'open' AND closed_at IS NULL AND closed_by IS NULL)
        OR (status = 'closed' AND closed_at IS NOT NULL AND closed_by IS NOT NULL)),
    CONSTRAINT accounting_period_no_overlap EXCLUDE USING gist (
        ledger_id WITH =,
        daterange(period_start, period_end, '[]') WITH &&)
);

CREATE INDEX accounting_period_by_ledger ON ledger.accounting_period (tenant_id, ledger_id, period_start);
