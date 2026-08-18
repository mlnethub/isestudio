-- =============================================================================
-- 003_apply_ef_constraints.sql
--
-- Promotes the new guid_id column to the primary key of every business
-- table (dropping the original bigint `id` PK), and adds the unique index
-- on legacy_id. After this step the schema matches the EF Core TPC model
-- (Guid PK + bigint LegacyId compat column).
--
-- The script is idempotent:
--   * If a PK on guid_id already exists the ALTER is skipped.
--   * If the old bigint PK has already been dropped the DROP is skipped.
--   * Re-running the migration is a no-op.
-- =============================================================================

-- Helper: idempotent PK swap. Drops the legacy bigint PK (if it still
-- exists) and adds the new guid_id PK (if it doesn't already exist).
-- Re-running the migration is a no-op.
CREATE OR REPLACE FUNCTION _ontopilot_swap_pk(t text)
    RETURNS void AS $$
DECLARE
    legacy_name text;
    new_name text;
BEGIN
    legacy_name := t || '_pkey';
    new_name := 'PK_' || t;

    -- Drop the original bigint PK if it is still present.
    EXECUTE format('ALTER TABLE %I DROP CONSTRAINT IF EXISTS %I', t, legacy_name);

    -- Add the new guid_id PK if it isn't already there.
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint c
        JOIN pg_class r ON c.conrelid = r.oid
        WHERE c.contype = 'p'
          AND r.relname = t
    ) THEN
        EXECUTE format('ALTER TABLE %I ADD CONSTRAINT %I PRIMARY KEY (guid_id)', t, new_name);
    END IF;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------
-- users
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('users');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_users_legacy_id" ON "users" ("legacy_id");

-- ---------------------------------------------------------------------------
-- provider
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('provider');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_provider_legacy_id" ON "provider" ("legacy_id");

-- ---------------------------------------------------------------------------
-- knowledgesystem
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('knowledgesystem');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_knowledgesystem_legacy_id" ON "knowledgesystem" ("legacy_id");

-- ---------------------------------------------------------------------------
-- authsession
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('authsession');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_authsession_legacy_id" ON "authsession" ("legacy_id");

-- ---------------------------------------------------------------------------
-- ksgrant
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('ksgrant');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_ksgrant_legacy_id" ON "ksgrant" ("legacy_id");

-- ---------------------------------------------------------------------------
-- knowledgepromptoverride
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('knowledgepromptoverride');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_kpo_legacy_id" ON "knowledgepromptoverride" ("legacy_id");

-- ---------------------------------------------------------------------------
-- knowledgeapitoken
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('knowledgeapitoken');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_kat_legacy_id" ON "knowledgeapitoken" ("legacy_id");

-- ---------------------------------------------------------------------------
-- mcpusertoken
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('mcpusertoken');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_mcp_legacy_id" ON "mcpusertoken" ("legacy_id");

-- ---------------------------------------------------------------------------
-- document
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('document');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_document_legacy_id" ON "document" ("legacy_id");

-- ---------------------------------------------------------------------------
-- chunk
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('chunk');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_chunk_legacy_id" ON "chunk" ("legacy_id");

-- ---------------------------------------------------------------------------
-- systemconfig
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('systemconfig');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_systemconfig_legacy_id" ON "systemconfig" ("legacy_id");

-- ---------------------------------------------------------------------------
-- extractionjob
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('extractionjob');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_extractionjob_legacy_id" ON "extractionjob" ("legacy_id");

-- ---------------------------------------------------------------------------
-- auditevent
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('auditevent');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_auditevent_legacy_id" ON "auditevent" ("legacy_id");

-- ---------------------------------------------------------------------------
-- axiomprovenance
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('axiomprovenance');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_axiomprovenance_legacy_id" ON "axiomprovenance" ("legacy_id");

-- ---------------------------------------------------------------------------
-- aboxprovenance
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('aboxprovenance');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_aboxprovenance_legacy_id" ON "aboxprovenance" ("legacy_id");

-- ---------------------------------------------------------------------------
-- ontologyrelease
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('ontologyrelease');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_ontologyrelease_legacy_id" ON "ontologyrelease" ("legacy_id");

-- ---------------------------------------------------------------------------
-- releasedeployment
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('releasedeployment');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_releasedeployment_legacy_id" ON "releasedeployment" ("legacy_id");

-- ---------------------------------------------------------------------------
-- releasestatementprovenance
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('releasestatementprovenance');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_rsp_legacy_id" ON "releasestatementprovenance" ("legacy_id");

-- ---------------------------------------------------------------------------
-- exportjob
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('exportjob');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_exportjob_legacy_id" ON "exportjob" ("legacy_id");

-- ---------------------------------------------------------------------------
-- conflict
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('conflict');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_conflict_legacy_id" ON "conflict" ("legacy_id");

-- ---------------------------------------------------------------------------
-- entityresolution
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('entityresolution');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_entityresolution_legacy_id" ON "entityresolution" ("legacy_id");

-- ---------------------------------------------------------------------------
-- termproposal
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('termproposal');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_termproposal_legacy_id" ON "termproposal" ("legacy_id");

-- ---------------------------------------------------------------------------
-- tboxreconciliation
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('tboxreconciliation');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_tboxreconciliation_legacy_id" ON "tboxreconciliation" ("legacy_id");

-- ---------------------------------------------------------------------------
-- validationdecision
-- ---------------------------------------------------------------------------
SELECT _ontopilot_swap_pk('validationdecision');
CREATE UNIQUE INDEX IF NOT EXISTS "ix_validationdecision_legacy_id" ON "validationdecision" ("legacy_id");