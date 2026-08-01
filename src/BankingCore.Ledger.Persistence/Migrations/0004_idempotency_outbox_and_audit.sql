-- 0004 Idempotency receipts, transactional outbox, and audit provenance.

-- Idempotency is scoped by tenant, principal, operation, and key, and stores the canonical request
-- fingerprint plus the original terminal outcome (docs/architecture/ledger.md, "Idempotency").
--
-- The receipt is inserted inside the same transaction as the journal it describes, so the unique
-- index is what actually serializes two concurrent identical commands: the loser blocks on the
-- index until the winner commits, then reads the committed outcome. A separate "in progress"
-- state would break the single atomic boundary the ledger constitution requires.
CREATE TABLE ledger.idempotency_receipt (
    receipt_id          uuid        PRIMARY KEY,
    tenant_id           uuid        NOT NULL,
    principal_id        text        NOT NULL,
    operation           text        NOT NULL,
    idempotency_key     text        NOT NULL,
    request_fingerprint bytea       NOT NULL,
    outcome             text        NOT NULL,
    outcome_journal_id  uuid        NULL REFERENCES ledger.journal (journal_id),
    outcome_code        text        NULL,
    outcome_detail      text        NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    expires_at          timestamptz NOT NULL,
    CONSTRAINT idempotency_receipt_scope_unique
        UNIQUE (tenant_id, principal_id, operation, idempotency_key),
    CONSTRAINT idempotency_receipt_key_length CHECK (char_length(idempotency_key) BETWEEN 1 AND 128),
    CONSTRAINT idempotency_receipt_fingerprint_length CHECK (octet_length(request_fingerprint) = 32),
    CONSTRAINT idempotency_receipt_outcome_known CHECK (outcome IN ('succeeded', 'failed')),
    CONSTRAINT idempotency_receipt_outcome_shape CHECK (
        (outcome = 'succeeded' AND outcome_journal_id IS NOT NULL AND outcome_code IS NULL)
        OR (outcome = 'failed' AND outcome_journal_id IS NULL AND outcome_code IS NOT NULL)),
    -- Retention must exceed every credible client retry window and audit need. Expiry frees the
    -- key for reuse; it never frees the journal identifier, which stays unique permanently.
    CONSTRAINT idempotency_receipt_expiry_after_creation CHECK (expires_at > created_at)
);

CREATE INDEX idempotency_receipt_expiry ON ledger.idempotency_receipt (expires_at);

CREATE TRIGGER idempotency_receipt_is_insert_only
    BEFORE UPDATE ON ledger.idempotency_receipt
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();

-- Transactional outbox. A row is written in the same transaction as the fact it describes, so a
-- broker acknowledgment can never precede or replace the database commit (evaluations AG-008,
-- AG-009). Publication after commit is at least once; consumers deduplicate on event_id.
CREATE TABLE ledger.outbox_message (
    message_id            uuid        PRIMARY KEY,
    tenant_id             uuid        NOT NULL,
    journal_id            uuid        NOT NULL REFERENCES ledger.journal (journal_id),
    event_type            text        NOT NULL,
    event_schema_version  integer     NOT NULL,
    source                text        NOT NULL,
    subject               text        NOT NULL,
    partition_key         text        NOT NULL,
    data_classification   text        NOT NULL,
    correlation_id        uuid        NOT NULL,
    causation_id          uuid        NULL,
    occurred_at           timestamptz NOT NULL,
    payload               jsonb       NOT NULL,
    created_at            timestamptz NOT NULL DEFAULT now(),
    attempt_count         integer     NOT NULL DEFAULT 0,
    locked_until          timestamptz NULL,
    published_at          timestamptz NULL,
    quarantined_at        timestamptz NULL,
    quarantine_reason     text        NULL,
    -- Exactly one outbox row per publishable fact and event type. This is what makes "outbox
    -- coverage exists for every committed publishable fact" a checkable reconciliation assertion.
    CONSTRAINT outbox_message_fact_unique UNIQUE (journal_id, event_type),
    CONSTRAINT outbox_message_schema_version_positive CHECK (event_schema_version >= 1),
    CONSTRAINT outbox_message_attempts_non_negative CHECK (attempt_count >= 0),
    CONSTRAINT outbox_message_classification_known CHECK (
        data_classification IN ('public', 'internal', 'confidential', 'restricted')),
    CONSTRAINT outbox_message_quarantine_shape CHECK (
        (quarantined_at IS NULL AND quarantine_reason IS NULL)
        OR (quarantined_at IS NOT NULL AND quarantine_reason IS NOT NULL)),
    -- A quarantined message is visible and replayable, never silently dropped.
    CONSTRAINT outbox_message_not_published_and_quarantined CHECK (
        published_at IS NULL OR quarantined_at IS NULL)
);

CREATE INDEX outbox_message_pending
    ON ledger.outbox_message (created_at)
    WHERE published_at IS NULL AND quarantined_at IS NULL;

CREATE TRIGGER outbox_message_no_delete
    BEFORE DELETE ON ledger.outbox_message
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();

-- Audit provenance for the decision, written inside the same atomic boundary as the decision it
-- explains. Detail is a minimized structured document: never a raw request body, credential, or
-- unmasked restricted value (evaluation AG-012).
CREATE TABLE ledger.audit_event (
    audit_id                  uuid        PRIMARY KEY,
    tenant_id                 uuid        NOT NULL,
    occurred_at               timestamptz NOT NULL,
    actor_id                  text        NOT NULL,
    actor_type                text        NOT NULL,
    action                    text        NOT NULL,
    resource_type             text        NOT NULL,
    resource_id               text        NOT NULL,
    authorization_decision_id uuid        NOT NULL,
    outcome                   text        NOT NULL,
    outcome_code              text        NULL,
    correlation_id            uuid        NOT NULL,
    detail                    jsonb       NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT audit_event_actor_type_known CHECK (actor_type IN ('user', 'workload')),
    CONSTRAINT audit_event_outcome_known CHECK (outcome IN ('allowed', 'denied', 'rejected', 'failed'))
);

CREATE INDEX audit_event_by_resource ON ledger.audit_event (tenant_id, resource_type, resource_id);
CREATE INDEX audit_event_by_correlation ON ledger.audit_event (tenant_id, correlation_id);

CREATE TRIGGER audit_event_is_insert_only
    BEFORE UPDATE OR DELETE ON ledger.audit_event
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();
