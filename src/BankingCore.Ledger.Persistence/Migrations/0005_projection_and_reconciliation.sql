-- 0005 Statement projection, consumer inbox, and reconciliation records.

-- Derived read model. Source: ledger.journal and ledger.posting. Checkpoint: the per-ledger
-- sequence in projection_checkpoint. Consistency: eventually consistent, never authoritative for a
-- financial decision. Rebuild: truncate the entries for a ledger, reset its checkpoint to zero, and
-- replay; the gap-free sequence makes the replay exact
-- (docs/architecture/data-and-consistency.md, "Data ownership").
CREATE TABLE ledger_projection.statement_entry (
    posting_id          uuid          PRIMARY KEY,
    tenant_id           uuid          NOT NULL,
    ledger_id           uuid          NOT NULL,
    account_id          uuid          NOT NULL,
    asset_id            uuid          NOT NULL,
    journal_id          uuid          NOT NULL,
    ledger_sequence     bigint        NOT NULL,
    posting_order       smallint      NOT NULL,
    direction           text          NOT NULL,
    amount              numeric(38,0) NOT NULL,
    running_debit_total numeric(38,0) NOT NULL,
    running_credit_total numeric(38,0) NOT NULL,
    transaction_type    text          NOT NULL,
    reverses_journal_id uuid          NULL,
    booking_date        date          NOT NULL,
    value_date          date          NOT NULL,
    effective_at        timestamptz   NOT NULL,
    posted_at           timestamptz   NOT NULL,
    projected_at        timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT statement_entry_position_unique UNIQUE (account_id, ledger_sequence, posting_order),
    CONSTRAINT statement_entry_direction_known CHECK (direction IN ('debit', 'credit')),
    CONSTRAINT statement_entry_amount_positive CHECK (amount > 0),
    CONSTRAINT statement_entry_running_non_negative CHECK (
        running_debit_total >= 0 AND running_credit_total >= 0)
);

CREATE INDEX statement_entry_by_account
    ON ledger_projection.statement_entry (tenant_id, account_id, ledger_sequence, posting_order);

CREATE TABLE ledger_projection.projection_checkpoint (
    projection_name      text        NOT NULL,
    ledger_id            uuid        NOT NULL,
    tenant_id            uuid        NOT NULL,
    last_ledger_sequence bigint      NOT NULL DEFAULT 0,
    updated_at           timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (projection_name, ledger_id),
    CONSTRAINT projection_checkpoint_non_negative CHECK (last_ledger_sequence >= 0)
);

-- Consumer-side deduplication. Delivery is at least once, so a consumer records the event
-- identifiers it has already applied and makes handling idempotent
-- (docs/architecture/data-and-consistency.md, "Delivery semantics").
CREATE TABLE ledger_projection.inbox_message (
    consumer_name  text        NOT NULL,
    event_id       uuid        NOT NULL,
    tenant_id      uuid        NOT NULL,
    first_seen_at  timestamptz NOT NULL DEFAULT now(),
    delivery_count integer     NOT NULL DEFAULT 1,
    PRIMARY KEY (consumer_name, event_id),
    CONSTRAINT inbox_message_delivery_positive CHECK (delivery_count >= 1)
);

-- Reconciliation. A difference creates a durable break with severity, owner, evidence, and a
-- resolution path. Automated repair may propose but never rewrites immutable facts
-- (docs/architecture/ledger.md, "Reconciliation and proofs"; evaluation AG-014).
CREATE TABLE ledger.reconciliation_run (
    run_id          uuid        PRIMARY KEY,
    tenant_id       uuid        NOT NULL,
    ledger_id       uuid        NULL,
    started_at      timestamptz NOT NULL,
    completed_at    timestamptz NULL,
    checks_executed integer     NOT NULL DEFAULT 0,
    breaks_found    integer     NOT NULL DEFAULT 0,
    source_revision text        NOT NULL,
    CONSTRAINT reconciliation_run_counts_non_negative CHECK (
        checks_executed >= 0 AND breaks_found >= 0)
);

CREATE TABLE ledger.reconciliation_break (
    break_id            uuid        PRIMARY KEY,
    run_id              uuid        NOT NULL REFERENCES ledger.reconciliation_run (run_id),
    tenant_id           uuid        NOT NULL,
    check_name          text        NOT NULL,
    severity            text        NOT NULL,
    subject             text        NOT NULL,
    detail              jsonb       NOT NULL,
    status              text        NOT NULL DEFAULT 'open',
    owner               text        NULL,
    detected_at         timestamptz NOT NULL,
    resolved_at         timestamptz NULL,
    resolution_evidence text        NULL,
    CONSTRAINT reconciliation_break_severity_known CHECK (
        severity IN ('critical', 'high', 'medium', 'low')),
    CONSTRAINT reconciliation_break_status_known CHECK (
        status IN ('open', 'investigating', 'resolved', 'accepted')),
    CONSTRAINT reconciliation_break_resolution_shape CHECK (
        (status IN ('open', 'investigating') AND resolved_at IS NULL AND resolution_evidence IS NULL)
        OR (status IN ('resolved', 'accepted') AND resolved_at IS NOT NULL AND resolution_evidence IS NOT NULL))
);

CREATE INDEX reconciliation_break_open
    ON ledger.reconciliation_break (tenant_id, severity, detected_at)
    WHERE status IN ('open', 'investigating');

CREATE TRIGGER reconciliation_break_no_delete
    BEFORE DELETE ON ledger.reconciliation_break
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();
