-- =============================================================================
-- rollback.sql
--
-- Reverse the migration applied by 001/002/003. The original bigint `id`
-- primary key was never dropped (it is still on every business table as
-- a regular bigint column), so rollback just needs to:
--
--   1. Drop the new PK constraint (on guid_id) from every business table.
--   2. Drop the unique index on legacy_id from every business table.
--   3. Re-add the original bigint `id` PK constraint.
--   4. Cast every uuid-typed FK column back to bigint, populating it
--      with the parent's id (looked up by the parent's guid_id).
--   5. Drop the guid_id and legacy_id columns.
--
-- After this script the schema is back to the original Python /
-- SQLAlchemy shape and the data is fully readable by the Python backend.
-- =============================================================================

-- ---------------------------------------------------------------------------
-- Drop the new PKs and the unique indexes on legacy_id.
-- ---------------------------------------------------------------------------
ALTER TABLE "users"                    DROP CONSTRAINT IF EXISTS "PK_users";
ALTER TABLE "provider"                 DROP CONSTRAINT IF EXISTS "PK_provider";
ALTER TABLE "knowledgesystem"          DROP CONSTRAINT IF EXISTS "PK_knowledgesystem";
ALTER TABLE "authsession"              DROP CONSTRAINT IF EXISTS "PK_authsession";
ALTER TABLE "ksgrant"                  DROP CONSTRAINT IF EXISTS "PK_ksgrant";
ALTER TABLE "knowledgepromptoverride"  DROP CONSTRAINT IF EXISTS "PK_kpo";
ALTER TABLE "knowledgeapitoken"        DROP CONSTRAINT IF EXISTS "PK_kat";
ALTER TABLE "mcpusertoken"             DROP CONSTRAINT IF EXISTS "PK_mcp";
ALTER TABLE "document"                 DROP CONSTRAINT IF EXISTS "PK_document";
ALTER TABLE "chunk"                    DROP CONSTRAINT IF EXISTS "PK_chunk";
ALTER TABLE "systemconfig"             DROP CONSTRAINT IF EXISTS "PK_systemconfig";
ALTER TABLE "extractionjob"            DROP CONSTRAINT IF EXISTS "PK_extractionjob";
ALTER TABLE "auditevent"               DROP CONSTRAINT IF EXISTS "PK_auditevent";
ALTER TABLE "axiomprovenance"          DROP CONSTRAINT IF EXISTS "PK_axiomprovenance";
ALTER TABLE "aboxprovenance"           DROP CONSTRAINT IF EXISTS "PK_aboxprovenance";
ALTER TABLE "ontologyrelease"          DROP CONSTRAINT IF EXISTS "PK_ontologyrelease";
ALTER TABLE "releasedeployment"        DROP CONSTRAINT IF EXISTS "PK_releasedeployment";
ALTER TABLE "releasestatementprovenance" DROP CONSTRAINT IF EXISTS "PK_rsp";
ALTER TABLE "exportjob"                DROP CONSTRAINT IF EXISTS "PK_exportjob";
ALTER TABLE "conflict"                 DROP CONSTRAINT IF EXISTS "PK_conflict";
ALTER TABLE "entityresolution"         DROP CONSTRAINT IF EXISTS "PK_entityresolution";
ALTER TABLE "termproposal"             DROP CONSTRAINT IF EXISTS "PK_termproposal";
ALTER TABLE "tboxreconciliation"       DROP CONSTRAINT IF EXISTS "PK_tboxreconciliation";
ALTER TABLE "validationdecision"       DROP CONSTRAINT IF EXISTS "PK_validationdecision";

DROP INDEX IF EXISTS "ix_users_legacy_id";
DROP INDEX IF EXISTS "ix_provider_legacy_id";
DROP INDEX IF EXISTS "ix_knowledgesystem_legacy_id";
DROP INDEX IF EXISTS "ix_authsession_legacy_id";
DROP INDEX IF EXISTS "ix_ksgrant_legacy_id";
DROP INDEX IF EXISTS "ix_kpo_legacy_id";
DROP INDEX IF EXISTS "ix_kat_legacy_id";
DROP INDEX IF EXISTS "ix_mcp_legacy_id";
DROP INDEX IF EXISTS "ix_document_legacy_id";
DROP INDEX IF EXISTS "ix_chunk_legacy_id";
DROP INDEX IF EXISTS "ix_systemconfig_legacy_id";
DROP INDEX IF EXISTS "ix_extractionjob_legacy_id";
DROP INDEX IF EXISTS "ix_auditevent_legacy_id";
DROP INDEX IF EXISTS "ix_axiomprovenance_legacy_id";
DROP INDEX IF EXISTS "ix_aboxprovenance_legacy_id";
DROP INDEX IF EXISTS "ix_ontologyrelease_legacy_id";
DROP INDEX IF EXISTS "ix_releasedeployment_legacy_id";
DROP INDEX IF EXISTS "ix_rsp_legacy_id";
DROP INDEX IF EXISTS "ix_exportjob_legacy_id";
DROP INDEX IF EXISTS "ix_conflict_legacy_id";
DROP INDEX IF EXISTS "ix_entityresolution_legacy_id";
DROP INDEX IF EXISTS "ix_termproposal_legacy_id";
DROP INDEX IF EXISTS "ix_tboxreconciliation_legacy_id";
DROP INDEX IF EXISTS "ix_validationdecision_legacy_id";

-- ---------------------------------------------------------------------------
-- Restore the original bigint PKs.
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'users'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "users" ADD CONSTRAINT "users_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'provider'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "provider" ADD CONSTRAINT "provider_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'knowledgesystem'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "knowledgesystem" ADD CONSTRAINT "knowledgesystem_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'authsession'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "authsession" ADD CONSTRAINT "authsession_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'ksgrant'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "ksgrant" ADD CONSTRAINT "ksgrant_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'knowledgepromptoverride'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "knowledgepromptoverride" ADD CONSTRAINT "knowledgepromptoverride_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'knowledgeapitoken'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "knowledgeapitoken" ADD CONSTRAINT "knowledgeapitoken_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'mcpusertoken'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "mcpusertoken" ADD CONSTRAINT "mcpusertoken_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'document'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "document" ADD CONSTRAINT "document_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'chunk'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "chunk" ADD CONSTRAINT "chunk_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'systemconfig'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "systemconfig" ADD CONSTRAINT "systemconfig_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'extractionjob'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "extractionjob" ADD CONSTRAINT "extractionjob_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'auditevent'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "auditevent" ADD CONSTRAINT "auditevent_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'axiomprovenance'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "axiomprovenance" ADD CONSTRAINT "axiomprovenance_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'aboxprovenance'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "aboxprovenance" ADD CONSTRAINT "aboxprovenance_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'ontologyrelease'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "ontologyrelease" ADD CONSTRAINT "ontologyrelease_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'releasedeployment'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "releasedeployment" ADD CONSTRAINT "releasedeployment_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'releasestatementprovenance'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "releasestatementprovenance" ADD CONSTRAINT "releasestatementprovenance_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'exportjob'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "exportjob" ADD CONSTRAINT "exportjob_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'conflict'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "conflict" ADD CONSTRAINT "conflict_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'entityresolution'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "entityresolution" ADD CONSTRAINT "entityresolution_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'termproposal'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "termproposal" ADD CONSTRAINT "termproposal_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'tboxreconciliation'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "tboxreconciliation" ADD CONSTRAINT "tboxreconciliation_pkey" PRIMARY KEY ("id");
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'public' AND table_name = 'validationdecision'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE "validationdecision" ADD CONSTRAINT "validationdecision_pkey" PRIMARY KEY ("id");
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- Cast every uuid-typed FK column back to bigint, populating it with
-- the parent's id (looked up by the parent's guid_id). For each
-- (child, fk, parent) tuple we snapshot, drop the FK constraint, cast
-- the column, backfill, conditionally re-add NOT NULL, and re-add the
-- original <child_table>_<child_fk_col>_fkey constraint with the original
-- ON DELETE rule so SQLAlchemy reflection finds the relations.
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    rec record;
    snap_table text;
    original_fk_name text;
    original_delete_rule text;
    was_not_null_rb boolean;
    not_null_clause text;
    delete_clause text;
BEGIN
    FOR rec IN
        SELECT
            tc.table_name   AS child_table,
            kcu.column_name AS child_fk_col,
            ccu.table_name  AS parent_table,
            tc.constraint_name AS fk_name,
            rc.delete_rule  AS on_delete
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu
          ON tc.constraint_name = kcu.constraint_name
         AND tc.table_schema = kcu.table_schema
        JOIN information_schema.constraint_column_usage ccu
          ON ccu.constraint_name = tc.constraint_name
         AND ccu.table_schema = tc.table_schema
        JOIN information_schema.referential_constraints rc
          ON rc.constraint_name = tc.constraint_name
         AND rc.constraint_schema = tc.table_schema
        JOIN information_schema.columns c
          ON c.table_schema = 'public'
         AND c.table_name = tc.table_name
         AND c.column_name = kcu.column_name
         AND c.data_type = 'uuid'
        WHERE tc.constraint_type = 'FOREIGN KEY'
          AND tc.table_schema = 'public'
    LOOP
        snap_table := '_fk_snap_rb_' || rec.child_table || '_' || rec.child_fk_col;
        EXECUTE format(
            'CREATE TEMP TABLE %I ON COMMIT DROP AS '
            'SELECT id AS row_id, %I AS fk_value FROM %I',
            snap_table, rec.child_fk_col, rec.child_table
        );

        -- Drop the existing (forward-migration) FK constraint.
        original_fk_name := rec.fk_name;
        EXECUTE format(
            'ALTER TABLE %I DROP CONSTRAINT IF EXISTS %I',
            rec.child_table, original_fk_name
        );

        -- Capture original NOT NULL state so we can re-add it.
        SELECT c.is_nullable = 'NO' INTO was_not_null_rb
        FROM information_schema.columns c
        WHERE c.table_schema = 'public'
          AND c.table_name = rec.child_table
          AND c.column_name = rec.child_fk_col;

        -- Cast uuid back to bigint. NOT NULL would prevent the cast
        -- (USING NULL fills with NULL), so drop it conditionally first.
        IF was_not_null_rb THEN
            EXECUTE format(
                'ALTER TABLE %I ALTER COLUMN %I DROP NOT NULL',
                rec.child_table, rec.child_fk_col
            );
        END IF;
        EXECUTE format(
            'ALTER TABLE %I ALTER COLUMN %I TYPE bigint USING NULL',
            rec.child_table, rec.child_fk_col
        );
        EXECUTE format(
            'UPDATE %I c SET %I = p.id FROM %I s '
            'JOIN %I p ON p.guid_id = s.fk_value '
            'WHERE c.id = s.row_id',
            rec.child_table, rec.child_fk_col, snap_table, rec.parent_table
        );
        IF was_not_null_rb THEN
            EXECUTE format(
                'ALTER TABLE %I ALTER COLUMN %I SET NOT NULL',
                rec.child_table, rec.child_fk_col
            );
        END IF;

        -- Re-add the original FK constraint using the Postgres-default
        -- <child_table>_<child_fk_col>_fkey naming so SQLAlchemy
        -- reflection finds the relationship. Preserve the original
        -- ON DELETE rule; PostgreSQL stores it as 'RESTRICT', 'CASCADE',
        -- 'NO ACTION', 'SET NULL', or 'SET DEFAULT'.
        original_delete_rule := UPPER(rec.on_delete);
        delete_clause := CASE original_delete_rule
            WHEN 'CASCADE'    THEN 'ON DELETE CASCADE'
            WHEN 'SET NULL'   THEN 'ON DELETE SET NULL'
            WHEN 'SET DEFAULT' THEN 'ON DELETE SET DEFAULT'
            WHEN 'RESTRICT'   THEN 'ON DELETE RESTRICT'
            ELSE '' -- NO ACTION is the default and can be omitted
        END;
        EXECUTE format(
            'ALTER TABLE %I ADD CONSTRAINT %I '
            'FOREIGN KEY (%I) REFERENCES %I(id) %s',
            rec.child_table,
            rec.child_table || '_' || rec.child_fk_col || '_fkey',
            rec.child_fk_col,
            rec.parent_table,
            delete_clause
        );
    END LOOP;
END $$;

-- ---------------------------------------------------------------------------
-- Drop the guid_id and legacy_id columns from every business table.
-- The original bigint `id` column is the only remaining identity column.
-- ---------------------------------------------------------------------------
ALTER TABLE "users"                    DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "users"                    DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "provider"                 DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "provider"                 DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "knowledgesystem"          DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "knowledgesystem"          DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "authsession"              DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "authsession"              DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "ksgrant"                  DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "ksgrant"                  DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "knowledgepromptoverride"  DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "knowledgepromptoverride"  DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "knowledgeapitoken"        DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "knowledgeapitoken"        DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "mcpusertoken"             DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "mcpusertoken"             DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "document"                 DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "document"                 DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "chunk"                    DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "chunk"                    DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "systemconfig"             DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "systemconfig"             DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "extractionjob"            DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "extractionjob"            DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "auditevent"               DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "auditevent"               DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "axiomprovenance"          DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "axiomprovenance"          DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "aboxprovenance"           DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "aboxprovenance"           DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "ontologyrelease"          DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "ontologyrelease"          DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "releasedeployment"        DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "releasedeployment"        DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "releasestatementprovenance" DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "releasestatementprovenance" DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "exportjob"                DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "exportjob"                DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "conflict"                 DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "conflict"                 DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "entityresolution"         DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "entityresolution"         DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "termproposal"             DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "termproposal"             DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "tboxreconciliation"       DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "tboxreconciliation"       DROP COLUMN IF EXISTS "legacy_id";
ALTER TABLE "validationdecision"       DROP COLUMN IF EXISTS "guid_id";
ALTER TABLE "validationdecision"       DROP COLUMN IF EXISTS "legacy_id";
