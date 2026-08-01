-- 0006 Row level security policies and least-privilege grants.
--
-- Two independent isolation controls apply to every tenant-scoped relation:
--   1. the application always derives the scope from the authenticated principal and binds it to
--      the transaction with SET LOCAL banking_core.tenant_id;
--   2. row level security makes the database itself invisible outside that scope, and fails closed
--      when the binding is missing because ledger.current_tenant_id() returns NULL.
-- Neither control trusts a tenant identifier taken from a request path or body (evaluation AG-011).
--
-- The schema owner is deliberately not forced under the policies: migrations, backup, and
-- restore-time verification need cross-tenant visibility. Application roles are never the owner.

REVOKE CREATE ON SCHEMA ledger FROM PUBLIC;
REVOKE CREATE ON SCHEMA ledger_projection FROM PUBLIC;

DO $$
DECLARE
    v_table text;
    v_tenant_scoped constant text[] := ARRAY[
        'ledger.ledger_book',
        'ledger.ledger_account',
        'ledger.account_balance',
        'ledger.ledger_sequence_state',
        'ledger.accounting_period',
        'ledger.journal',
        'ledger.posting',
        'ledger.idempotency_receipt',
        'ledger.outbox_message',
        'ledger.audit_event',
        'ledger.reconciliation_run',
        'ledger.reconciliation_break',
        'ledger_projection.statement_entry',
        'ledger_projection.projection_checkpoint',
        'ledger_projection.inbox_message'
    ];
BEGIN
    FOREACH v_table IN ARRAY v_tenant_scoped LOOP
        EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', v_table);
        EXECUTE format(
            'CREATE POLICY tenant_isolation ON %s '
            || 'USING (tenant_id = ledger.current_tenant_id()) '
            || 'WITH CHECK (tenant_id = ledger.current_tenant_id())',
            v_table);
    END LOOP;
END;
$$;

-- Assets are deployment-wide reference data with no tenant scope, so they are readable by every
-- application role and writable only by administration.
GRANT USAGE ON SCHEMA ledger TO
    banking_core_ledger_app, banking_core_admin_app, banking_core_projection_app, banking_core_readonly;
GRANT USAGE ON SCHEMA ledger_projection TO
    banking_core_projection_app, banking_core_readonly;
GRANT EXECUTE ON FUNCTION ledger.current_tenant_id() TO
    banking_core_ledger_app, banking_core_admin_app, banking_core_projection_app, banking_core_readonly;

-- Posting path. May commit financial facts and advance aggregates; may not administer the chart of
-- accounts, and has no UPDATE or DELETE on journals, postings, receipts, or audit records.
GRANT SELECT ON
    ledger.asset,
    ledger.ledger_book,
    ledger.ledger_account,
    ledger.account_balance,
    ledger.ledger_sequence_state,
    ledger.accounting_period,
    ledger.journal,
    ledger.posting,
    ledger.idempotency_receipt,
    ledger.outbox_message
    TO banking_core_ledger_app;
GRANT INSERT ON
    ledger.journal,
    ledger.posting,
    ledger.idempotency_receipt,
    ledger.outbox_message,
    ledger.audit_event
    TO banking_core_ledger_app;
GRANT UPDATE (debit_total, credit_total, posting_count, version, updated_at)
    ON ledger.account_balance TO banking_core_ledger_app;
GRANT UPDATE (next_sequence) ON ledger.ledger_sequence_state TO banking_core_ledger_app;

-- Ledger administration. Defines the chart of accounts and period controls; cannot post.
GRANT SELECT, INSERT, UPDATE ON
    ledger.asset,
    ledger.ledger_book,
    ledger.ledger_account,
    ledger.accounting_period
    TO banking_core_admin_app;
GRANT INSERT ON ledger.account_balance, ledger.ledger_sequence_state TO banking_core_admin_app;
GRANT INSERT ON ledger.audit_event TO banking_core_admin_app;
GRANT SELECT ON ledger.account_balance, ledger.journal, ledger.posting TO banking_core_admin_app;

-- Derivation worker: statement projection, outbox relay, and reconciliation. Reads authoritative
-- facts, owns derived stores, and records reconciliation breaks. Cannot post or administer.
GRANT SELECT ON
    ledger.asset,
    ledger.ledger_book,
    ledger.ledger_account,
    ledger.account_balance,
    ledger.accounting_period,
    ledger.journal,
    ledger.posting,
    ledger.outbox_message
    TO banking_core_projection_app;
GRANT UPDATE (attempt_count, locked_until, published_at, quarantined_at, quarantine_reason)
    ON ledger.outbox_message TO banking_core_projection_app;
GRANT SELECT, INSERT ON ledger.reconciliation_run TO banking_core_projection_app;
GRANT UPDATE (completed_at, checks_executed, breaks_found) ON ledger.reconciliation_run
    TO banking_core_projection_app;
GRANT SELECT, INSERT ON ledger.reconciliation_break TO banking_core_projection_app;
GRANT UPDATE (status, owner, resolved_at, resolution_evidence) ON ledger.reconciliation_break
    TO banking_core_projection_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON
    ledger_projection.statement_entry,
    ledger_projection.projection_checkpoint,
    ledger_projection.inbox_message
    TO banking_core_projection_app;

-- Query path.
GRANT SELECT ON ALL TABLES IN SCHEMA ledger TO banking_core_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA ledger_projection TO banking_core_readonly;
