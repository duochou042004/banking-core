-- 0001 Schemas, roles, and extensions.
--
-- Forward-only migration (docs/architecture/data-and-consistency.md, "Schema evolution").
-- Roles are cluster-scoped, so creation is conditional and idempotent. In a production profile a
-- DBA provisions these roles and their credentials out of band; the migration only guarantees they
-- exist with the intended attributes and no login capability of its own.

CREATE SCHEMA IF NOT EXISTS ledger;
COMMENT ON SCHEMA ledger IS
    'Authoritative financial facts: assets, ledgers, accounts, journals, postings, aggregates, '
    'idempotency receipts, outbox, audit, and reconciliation. Source of truth.';

CREATE SCHEMA IF NOT EXISTS ledger_projection;
COMMENT ON SCHEMA ledger_projection IS
    'Derived read models rebuilt from ledger facts. Never a source of truth for a financial decision.';

-- btree_gist lets the accounting period exclusion constraint combine uuid equality with a date
-- range overlap test, so overlapping periods are rejected by the database rather than by policy.
CREATE EXTENSION IF NOT EXISTS btree_gist;

-- Roles are cluster-scoped, but the migrator's advisory lock is database-scoped: two databases on
-- one cluster can migrate at the same time and both find the role missing. A check-then-create is
-- therefore racy no matter how it is written, so creation catches the collision instead. Anything
-- that touches a cluster-scoped object must be written this way.
DO $$
DECLARE
    v_role text;
BEGIN
    FOREACH v_role IN ARRAY ARRAY[
        'banking_core_ledger_app',
        'banking_core_admin_app',
        'banking_core_projection_app',
        'banking_core_readonly'
    ] LOOP
        BEGIN
            EXECUTE format('CREATE ROLE %I NOLOGIN', v_role);
        EXCEPTION
            WHEN duplicate_object OR unique_violation THEN
                -- Another migration on this cluster created it first, which is the intended result.
                NULL;
        END;
    END LOOP;
END;
$$;

COMMENT ON ROLE banking_core_ledger_app IS
    'Posting path. May insert journals, postings, receipts, outbox rows, and audit records, and may '
    'advance account aggregates. May not administer accounts or mutate posted facts.';
COMMENT ON ROLE banking_core_admin_app IS
    'Ledger administration. May define assets, ledgers, accounts, and accounting periods. May not '
    'insert postings. Segregation of duties per docs/architecture/ledger.md.';
COMMENT ON ROLE banking_core_projection_app IS
    'Derivation worker: statement projection, outbox relay, and reconciliation. Reads authoritative '
    'facts, owns the derived read models, and records reconciliation breaks. Cannot post or administer.';
COMMENT ON ROLE banking_core_readonly IS
    'Query path. Select only, still subject to row level security.';

-- Resolves the tenant bound to the current transaction. Returns NULL when the caller did not bind a
-- tenant, which makes every row level security policy fail closed. Client-supplied tenant
-- identifiers are never used directly; the API sets this from the authenticated principal
-- (docs/architecture/data-and-consistency.md, "Multi-tenancy and legal entities"; evaluation AG-011).
CREATE OR REPLACE FUNCTION ledger.current_tenant_id() RETURNS uuid
    LANGUAGE sql
    STABLE
    AS $$ SELECT nullif(current_setting('banking_core.tenant_id', true), '')::uuid $$;

-- Rejects any attempt to update or delete an insert-only relation, including by the table owner.
-- Posted facts are corrected with linked reversals, never edited (evaluations AG-002, AG-007).
CREATE OR REPLACE FUNCTION ledger.reject_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION
        'relation %.% is insert-only; correct posted facts with a linked reversal, never an edit',
        TG_TABLE_SCHEMA, TG_TABLE_NAME
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$;
