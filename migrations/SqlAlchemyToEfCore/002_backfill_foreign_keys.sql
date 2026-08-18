-- =============================================================================
-- 002_backfill_foreign_keys.sql
--
-- Rewrites every foreign-key column from the original bigint (referencing
-- the parent's bigint `id`) to uuid (referencing the parent's new
-- `guid_id`). The original bigint value in the FK column is used to look
-- up the parent's new guid_id.
--
-- For each FK column:
--   1. Snapshot the (row_id, bigint_fk_value) into a temp table so we
--      don't lose the original mapping when we wipe the column.
--   2. Look up the existing FK constraint name dynamically from
--      information_schema (Postgres auto-names the original
--      `<table>_<column>_fkey`, which is different from the EF Core
--      convention) and drop it.
--   3. DROP NOT NULL on the column if it was NOT NULL, otherwise
--      PostgreSQL refuses to change the type to uuid USING NULL.
--   4. ALTER COLUMN TYPE uuid USING NULL (clears the column).
--   5. Backfill by joining the snapshot against the parent on the
--      parent's `id`, reading the parent's `guid_id` and writing it
--      into the child's FK column.
--   6. Re-add the NOT NULL constraint if the column was originally
--      NOT NULL.
--   7. Re-add the FK constraint referencing the parent's guid_id
--      (named per the EF Core convention FK_<table>_<parent>_<col>).
--
-- Every step is idempotent: if the column is already uuid, the script
-- skips the rewrite. The script can be re-run after a partial apply.
-- =============================================================================

-- Helper: return the actual name of the FK constraint on (table, col),
-- or NULL if there is no FK on that column. The Python / SQLAlchemy
-- schema names its inline REFERENCES constraints `<table>_<col>_fkey`
-- (Postgres default), which is different from the EF Core convention.
CREATE OR REPLACE FUNCTION _ontopilot_fk_constraint_name(t text, c text)
    RETURNS text AS $$
DECLARE
    name text;
BEGIN
    SELECT tc.constraint_name INTO name
    FROM information_schema.table_constraints tc
    JOIN information_schema.key_column_usage kcu
      ON tc.constraint_name = kcu.constraint_name
     AND tc.table_schema = kcu.table_schema
    WHERE tc.constraint_type = 'FOREIGN KEY'
      AND tc.table_schema = 'public'
      AND tc.table_name = t
      AND kcu.column_name = c
    LIMIT 1;
    RETURN name;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------
-- knowledgesystem.ownerid -> users
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'knowledgesystem'
      AND column_name = 'ownerid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'knowledgesystem'
          AND column_name = 'ownerid';
        CREATE TEMP TABLE _fk_snap_ks_ownerid ON COMMIT DROP AS
            SELECT id AS row_id, "ownerid" AS fk_value FROM "knowledgesystem";
        fk_name := _ontopilot_fk_constraint_name('knowledgesystem', 'ownerid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE knowledgesystem DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "knowledgesystem" ALTER COLUMN "ownerid" DROP NOT NULL;
        END IF;
        ALTER TABLE "knowledgesystem" ALTER COLUMN "ownerid" TYPE uuid USING NULL;
        UPDATE "knowledgesystem" ks
            SET "ownerid" = u.guid_id
            FROM _fk_snap_ks_ownerid s
            JOIN "users" u ON u.id = s.fk_value
            WHERE ks.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "knowledgesystem" ALTER COLUMN "ownerid" SET NOT NULL;
        END IF;
        ALTER TABLE "knowledgesystem"
            ADD CONSTRAINT "FK_knowledgesystem_users_OwnerId"
            FOREIGN KEY ("ownerid") REFERENCES "users"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- knowledgesystem.llmproviderid -> provider
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'knowledgesystem'
      AND column_name = 'llmproviderid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'knowledgesystem'
          AND column_name = 'llmproviderid';
        CREATE TEMP TABLE _fk_snap_ks_llmprov ON COMMIT DROP AS
            SELECT id AS row_id, "llmproviderid" AS fk_value FROM "knowledgesystem";
        fk_name := _ontopilot_fk_constraint_name('knowledgesystem', 'llmproviderid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE knowledgesystem DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "knowledgesystem" ALTER COLUMN "llmproviderid" DROP NOT NULL;
        END IF;
        ALTER TABLE "knowledgesystem" ALTER COLUMN "llmproviderid" TYPE uuid USING NULL;
        UPDATE "knowledgesystem" ks
            SET "llmproviderid" = p.guid_id
            FROM _fk_snap_ks_llmprov s
            JOIN "provider" p ON p.id = s.fk_value
            WHERE ks.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "knowledgesystem" ALTER COLUMN "llmproviderid" SET NOT NULL;
        END IF;
        ALTER TABLE "knowledgesystem"
            ADD CONSTRAINT "FK_knowledgesystem_provider_LlmProviderId"
            FOREIGN KEY ("llmproviderid") REFERENCES "provider"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- knowledgesystem.embeddingproviderid -> provider
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'knowledgesystem'
      AND column_name = 'embeddingproviderid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'knowledgesystem'
          AND column_name = 'embeddingproviderid';
        CREATE TEMP TABLE _fk_snap_ks_embprov ON COMMIT DROP AS
            SELECT id AS row_id, "embeddingproviderid" AS fk_value FROM "knowledgesystem";
        fk_name := _ontopilot_fk_constraint_name('knowledgesystem', 'embeddingproviderid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE knowledgesystem DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "knowledgesystem" ALTER COLUMN "embeddingproviderid" DROP NOT NULL;
        END IF;
        ALTER TABLE "knowledgesystem" ALTER COLUMN "embeddingproviderid" TYPE uuid USING NULL;
        UPDATE "knowledgesystem" ks
            SET "embeddingproviderid" = p.guid_id
            FROM _fk_snap_ks_embprov s
            JOIN "provider" p ON p.id = s.fk_value
            WHERE ks.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "knowledgesystem" ALTER COLUMN "embeddingproviderid" SET NOT NULL;
        END IF;
        ALTER TABLE "knowledgesystem"
            ADD CONSTRAINT "FK_knowledgesystem_provider_EmbeddingProviderId"
            FOREIGN KEY ("embeddingproviderid") REFERENCES "provider"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- authsession.userid -> users
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'authsession'
      AND column_name = 'userid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'authsession'
          AND column_name = 'userid';
        CREATE TEMP TABLE _fk_snap_as_userid ON COMMIT DROP AS
            SELECT id AS row_id, "userid" AS fk_value FROM "authsession";
        fk_name := _ontopilot_fk_constraint_name('authsession', 'userid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE authsession DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "authsession" ALTER COLUMN "userid" DROP NOT NULL;
        END IF;
        ALTER TABLE "authsession" ALTER COLUMN "userid" TYPE uuid USING NULL;
        UPDATE "authsession" a
            SET "userid" = u.guid_id
            FROM _fk_snap_as_userid s
            JOIN "users" u ON u.id = s.fk_value
            WHERE a.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "authsession" ALTER COLUMN "userid" SET NOT NULL;
        END IF;
        ALTER TABLE "authsession"
            ADD CONSTRAINT "FK_authsession_users_UserId"
            FOREIGN KEY ("userid") REFERENCES "users"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- ksgrant.knowledgesystemid -> knowledgesystem
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'ksgrant'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'ksgrant'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_kg_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "ksgrant";
        fk_name := _ontopilot_fk_constraint_name('ksgrant', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE ksgrant DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "ksgrant" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "ksgrant" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "ksgrant" k
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_kg_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE k.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "ksgrant" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "ksgrant"
            ADD CONSTRAINT "FK_ksgrant_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- ksgrant.userid -> users
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'ksgrant'
      AND column_name = 'userid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'ksgrant'
          AND column_name = 'userid';
        CREATE TEMP TABLE _fk_snap_kg_userid ON COMMIT DROP AS
            SELECT id AS row_id, "userid" AS fk_value FROM "ksgrant";
        fk_name := _ontopilot_fk_constraint_name('ksgrant', 'userid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE ksgrant DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "ksgrant" ALTER COLUMN "userid" DROP NOT NULL;
        END IF;
        ALTER TABLE "ksgrant" ALTER COLUMN "userid" TYPE uuid USING NULL;
        UPDATE "ksgrant" k
            SET "userid" = u.guid_id
            FROM _fk_snap_kg_userid s
            JOIN "users" u ON u.id = s.fk_value
            WHERE k.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "ksgrant" ALTER COLUMN "userid" SET NOT NULL;
        END IF;
        ALTER TABLE "ksgrant"
            ADD CONSTRAINT "FK_ksgrant_users_UserId"
            FOREIGN KEY ("userid") REFERENCES "users"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- knowledgepromptoverride.knowledgesystemid -> knowledgesystem
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'knowledgepromptoverride'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'knowledgepromptoverride'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_kpo_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "knowledgepromptoverride";
        fk_name := _ontopilot_fk_constraint_name('knowledgepromptoverride', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE knowledgepromptoverride DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "knowledgepromptoverride" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "knowledgepromptoverride" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "knowledgepromptoverride" k
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_kpo_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE k.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "knowledgepromptoverride" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "knowledgepromptoverride"
            ADD CONSTRAINT "FK_kpo_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- knowledgepromptoverride.updatedbyid -> users
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'knowledgepromptoverride'
      AND column_name = 'updatedbyid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'knowledgepromptoverride'
          AND column_name = 'updatedbyid';
        CREATE TEMP TABLE _fk_snap_kpo_ubyid ON COMMIT DROP AS
            SELECT id AS row_id, "updatedbyid" AS fk_value FROM "knowledgepromptoverride";
        fk_name := _ontopilot_fk_constraint_name('knowledgepromptoverride', 'updatedbyid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE knowledgepromptoverride DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "knowledgepromptoverride" ALTER COLUMN "updatedbyid" DROP NOT NULL;
        END IF;
        ALTER TABLE "knowledgepromptoverride" ALTER COLUMN "updatedbyid" TYPE uuid USING NULL;
        UPDATE "knowledgepromptoverride" k
            SET "updatedbyid" = u.guid_id
            FROM _fk_snap_kpo_ubyid s
            JOIN "users" u ON u.id = s.fk_value
            WHERE k.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "knowledgepromptoverride" ALTER COLUMN "updatedbyid" SET NOT NULL;
        END IF;
        ALTER TABLE "knowledgepromptoverride"
            ADD CONSTRAINT "FK_kpo_users_UpdatedById"
            FOREIGN KEY ("updatedbyid") REFERENCES "users"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- knowledgeapitoken.knowledgesystemid -> knowledgesystem
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'knowledgeapitoken'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'knowledgeapitoken'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_kat_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "knowledgeapitoken";
        fk_name := _ontopilot_fk_constraint_name('knowledgeapitoken', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE knowledgeapitoken DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "knowledgeapitoken" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "knowledgeapitoken" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "knowledgeapitoken" k
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_kat_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE k.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "knowledgeapitoken" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "knowledgeapitoken"
            ADD CONSTRAINT "FK_kat_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- knowledgeapitoken.createdbyid -> users
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'knowledgeapitoken'
      AND column_name = 'createdbyid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'knowledgeapitoken'
          AND column_name = 'createdbyid';
        CREATE TEMP TABLE _fk_snap_kat_cbyid ON COMMIT DROP AS
            SELECT id AS row_id, "createdbyid" AS fk_value FROM "knowledgeapitoken";
        fk_name := _ontopilot_fk_constraint_name('knowledgeapitoken', 'createdbyid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE knowledgeapitoken DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "knowledgeapitoken" ALTER COLUMN "createdbyid" DROP NOT NULL;
        END IF;
        ALTER TABLE "knowledgeapitoken" ALTER COLUMN "createdbyid" TYPE uuid USING NULL;
        UPDATE "knowledgeapitoken" k
            SET "createdbyid" = u.guid_id
            FROM _fk_snap_kat_cbyid s
            JOIN "users" u ON u.id = s.fk_value
            WHERE k.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "knowledgeapitoken" ALTER COLUMN "createdbyid" SET NOT NULL;
        END IF;
        ALTER TABLE "knowledgeapitoken"
            ADD CONSTRAINT "FK_kat_users_CreatedById"
            FOREIGN KEY ("createdbyid") REFERENCES "users"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- mcpusertoken.knowledgesystemid -> knowledgesystem
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'mcpusertoken'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'mcpusertoken'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_mcp_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "mcpusertoken";
        fk_name := _ontopilot_fk_constraint_name('mcpusertoken', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE mcpusertoken DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "mcpusertoken" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "mcpusertoken" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "mcpusertoken" m
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_mcp_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE m.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "mcpusertoken" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "mcpusertoken"
            ADD CONSTRAINT "FK_mcp_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- mcpusertoken.userid -> users
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'mcpusertoken'
      AND column_name = 'userid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'mcpusertoken'
          AND column_name = 'userid';
        CREATE TEMP TABLE _fk_snap_mcp_userid ON COMMIT DROP AS
            SELECT id AS row_id, "userid" AS fk_value FROM "mcpusertoken";
        fk_name := _ontopilot_fk_constraint_name('mcpusertoken', 'userid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE mcpusertoken DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "mcpusertoken" ALTER COLUMN "userid" DROP NOT NULL;
        END IF;
        ALTER TABLE "mcpusertoken" ALTER COLUMN "userid" TYPE uuid USING NULL;
        UPDATE "mcpusertoken" m
            SET "userid" = u.guid_id
            FROM _fk_snap_mcp_userid s
            JOIN "users" u ON u.id = s.fk_value
            WHERE m.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "mcpusertoken" ALTER COLUMN "userid" SET NOT NULL;
        END IF;
        ALTER TABLE "mcpusertoken"
            ADD CONSTRAINT "FK_mcp_users_UserId"
            FOREIGN KEY ("userid") REFERENCES "users"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- document.knowledgesystemid -> knowledgesystem
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'document'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'document'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_doc_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "document";
        fk_name := _ontopilot_fk_constraint_name('document', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE document DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "document" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "document" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "document" d
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_doc_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE d.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "document" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "document"
            ADD CONSTRAINT "FK_document_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- chunk.documentid -> document
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'chunk'
      AND column_name = 'documentid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'chunk'
          AND column_name = 'documentid';
        CREATE TEMP TABLE _fk_snap_chunk_docid ON COMMIT DROP AS
            SELECT id AS row_id, "documentid" AS fk_value FROM "chunk";
        fk_name := _ontopilot_fk_constraint_name('chunk', 'documentid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE chunk DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "chunk" ALTER COLUMN "documentid" DROP NOT NULL;
        END IF;
        ALTER TABLE "chunk" ALTER COLUMN "documentid" TYPE uuid USING NULL;
        UPDATE "chunk" c
            SET "documentid" = d.guid_id
            FROM _fk_snap_chunk_docid s
            JOIN "document" d ON d.id = s.fk_value
            WHERE c.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "chunk" ALTER COLUMN "documentid" SET NOT NULL;
        END IF;
        ALTER TABLE "chunk"
            ADD CONSTRAINT "FK_chunk_document_DocumentId"
            FOREIGN KEY ("documentid") REFERENCES "document"("guid_id") ON DELETE CASCADE;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- systemconfig.llmproviderid -> provider
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'systemconfig'
      AND column_name = 'llmproviderid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'systemconfig'
          AND column_name = 'llmproviderid';
        CREATE TEMP TABLE _fk_snap_sc_llmprov ON COMMIT DROP AS
            SELECT id AS row_id, "llmproviderid" AS fk_value FROM "systemconfig";
        fk_name := _ontopilot_fk_constraint_name('systemconfig', 'llmproviderid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE systemconfig DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "systemconfig" ALTER COLUMN "llmproviderid" DROP NOT NULL;
        END IF;
        ALTER TABLE "systemconfig" ALTER COLUMN "llmproviderid" TYPE uuid USING NULL;
        UPDATE "systemconfig" sc
            SET "llmproviderid" = p.guid_id
            FROM _fk_snap_sc_llmprov s
            JOIN "provider" p ON p.id = s.fk_value
            WHERE sc.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "systemconfig" ALTER COLUMN "llmproviderid" SET NOT NULL;
        END IF;
        ALTER TABLE "systemconfig"
            ADD CONSTRAINT "FK_systemconfig_provider_LlmProviderId"
            FOREIGN KEY ("llmproviderid") REFERENCES "provider"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- systemconfig.embeddingproviderid -> provider
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'systemconfig'
      AND column_name = 'embeddingproviderid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'systemconfig'
          AND column_name = 'embeddingproviderid';
        CREATE TEMP TABLE _fk_snap_sc_embprov ON COMMIT DROP AS
            SELECT id AS row_id, "embeddingproviderid" AS fk_value FROM "systemconfig";
        fk_name := _ontopilot_fk_constraint_name('systemconfig', 'embeddingproviderid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE systemconfig DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "systemconfig" ALTER COLUMN "embeddingproviderid" DROP NOT NULL;
        END IF;
        ALTER TABLE "systemconfig" ALTER COLUMN "embeddingproviderid" TYPE uuid USING NULL;
        UPDATE "systemconfig" sc
            SET "embeddingproviderid" = p.guid_id
            FROM _fk_snap_sc_embprov s
            JOIN "provider" p ON p.id = s.fk_value
            WHERE sc.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "systemconfig" ALTER COLUMN "embeddingproviderid" SET NOT NULL;
        END IF;
        ALTER TABLE "systemconfig"
            ADD CONSTRAINT "FK_systemconfig_provider_EmbeddingProviderId"
            FOREIGN KEY ("embeddingproviderid") REFERENCES "provider"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- extractionjob.knowledgesystemid -> knowledgesystem
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'extractionjob'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'extractionjob'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_ej_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "extractionjob";
        fk_name := _ontopilot_fk_constraint_name('extractionjob', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE extractionjob DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "extractionjob" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "extractionjob" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "extractionjob" e
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_ej_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE e.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "extractionjob" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "extractionjob"
            ADD CONSTRAINT "FK_extractionjob_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- auditevent.knowledgesystemid -> knowledgesystem
-- auditevent.actorid -> users
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'auditevent'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'auditevent'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_ae_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "auditevent";
        fk_name := _ontopilot_fk_constraint_name('auditevent', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE auditevent DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "auditevent" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "auditevent" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "auditevent" a
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_ae_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE a.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "auditevent" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "auditevent"
            ADD CONSTRAINT "FK_auditevent_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'auditevent'
      AND column_name = 'actorid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'auditevent'
          AND column_name = 'actorid';
        CREATE TEMP TABLE _fk_snap_ae_actorid ON COMMIT DROP AS
            SELECT id AS row_id, "actorid" AS fk_value FROM "auditevent";
        fk_name := _ontopilot_fk_constraint_name('auditevent', 'actorid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE auditevent DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "auditevent" ALTER COLUMN "actorid" DROP NOT NULL;
        END IF;
        ALTER TABLE "auditevent" ALTER COLUMN "actorid" TYPE uuid USING NULL;
        UPDATE "auditevent" a
            SET "actorid" = u.guid_id
            FROM _fk_snap_ae_actorid s
            JOIN "users" u ON u.id = s.fk_value
            WHERE a.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "auditevent" ALTER COLUMN "actorid" SET NOT NULL;
        END IF;
        ALTER TABLE "auditevent"
            ADD CONSTRAINT "FK_auditevent_users_ActorId"
            FOREIGN KEY ("actorid") REFERENCES "users"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- axiomprovenance.knowledgesystemid, .chunkid, .jobid, .auditeventid
-- aboxprovenance.knowledgesystemid, .chunkid, .jobid, .auditeventid
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'axiomprovenance'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'axiomprovenance'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_ap_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "axiomprovenance";
        fk_name := _ontopilot_fk_constraint_name('axiomprovenance', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE axiomprovenance DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "axiomprovenance" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "axiomprovenance" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "axiomprovenance" a
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_ap_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE a.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "axiomprovenance" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "axiomprovenance"
            ADD CONSTRAINT "FK_axiomprovenance_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'axiomprovenance'
      AND column_name = 'chunkid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'axiomprovenance'
          AND column_name = 'chunkid';
        CREATE TEMP TABLE _fk_snap_ap_chunkid ON COMMIT DROP AS
            SELECT id AS row_id, "chunkid" AS fk_value FROM "axiomprovenance";
        fk_name := _ontopilot_fk_constraint_name('axiomprovenance', 'chunkid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE axiomprovenance DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "axiomprovenance" ALTER COLUMN "chunkid" DROP NOT NULL;
        END IF;
        ALTER TABLE "axiomprovenance" ALTER COLUMN "chunkid" TYPE uuid USING NULL;
        UPDATE "axiomprovenance" a
            SET "chunkid" = c.guid_id
            FROM _fk_snap_ap_chunkid s
            JOIN "chunk" c ON c.id = s.fk_value
            WHERE a.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "axiomprovenance" ALTER COLUMN "chunkid" SET NOT NULL;
        END IF;
        ALTER TABLE "axiomprovenance"
            ADD CONSTRAINT "FK_axiomprovenance_chunk_ChunkId"
            FOREIGN KEY ("chunkid") REFERENCES "chunk"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'axiomprovenance'
      AND column_name = 'jobid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'axiomprovenance'
          AND column_name = 'jobid';
        CREATE TEMP TABLE _fk_snap_ap_jobid ON COMMIT DROP AS
            SELECT id AS row_id, "jobid" AS fk_value FROM "axiomprovenance";
        fk_name := _ontopilot_fk_constraint_name('axiomprovenance', 'jobid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE axiomprovenance DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "axiomprovenance" ALTER COLUMN "jobid" DROP NOT NULL;
        END IF;
        ALTER TABLE "axiomprovenance" ALTER COLUMN "jobid" TYPE uuid USING NULL;
        UPDATE "axiomprovenance" a
            SET "jobid" = e.guid_id
            FROM _fk_snap_ap_jobid s
            JOIN "extractionjob" e ON e.id = s.fk_value
            WHERE a.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "axiomprovenance" ALTER COLUMN "jobid" SET NOT NULL;
        END IF;
        ALTER TABLE "axiomprovenance"
            ADD CONSTRAINT "FK_axiomprovenance_extractionjob_JobId"
            FOREIGN KEY ("jobid") REFERENCES "extractionjob"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'axiomprovenance'
      AND column_name = 'auditeventid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'axiomprovenance'
          AND column_name = 'auditeventid';
        CREATE TEMP TABLE _fk_snap_ap_aeid ON COMMIT DROP AS
            SELECT id AS row_id, "auditeventid" AS fk_value FROM "axiomprovenance";
        fk_name := _ontopilot_fk_constraint_name('axiomprovenance', 'auditeventid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE axiomprovenance DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "axiomprovenance" ALTER COLUMN "auditeventid" DROP NOT NULL;
        END IF;
        ALTER TABLE "axiomprovenance" ALTER COLUMN "auditeventid" TYPE uuid USING NULL;
        UPDATE "axiomprovenance" a
            SET "auditeventid" = ae.guid_id
            FROM _fk_snap_ap_aeid s
            JOIN "auditevent" ae ON ae.id = s.fk_value
            WHERE a.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "axiomprovenance" ALTER COLUMN "auditeventid" SET NOT NULL;
        END IF;
        ALTER TABLE "axiomprovenance"
            ADD CONSTRAINT "FK_axiomprovenance_auditevent_AuditEventId"
            FOREIGN KEY ("auditeventid") REFERENCES "auditevent"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'aboxprovenance'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'aboxprovenance'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_abp_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "aboxprovenance";
        fk_name := _ontopilot_fk_constraint_name('aboxprovenance', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE aboxprovenance DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "aboxprovenance" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "aboxprovenance" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "aboxprovenance" a
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_abp_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE a.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "aboxprovenance" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "aboxprovenance"
            ADD CONSTRAINT "FK_aboxprovenance_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'aboxprovenance'
      AND column_name = 'chunkid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'aboxprovenance'
          AND column_name = 'chunkid';
        CREATE TEMP TABLE _fk_snap_abp_chunkid ON COMMIT DROP AS
            SELECT id AS row_id, "chunkid" AS fk_value FROM "aboxprovenance";
        fk_name := _ontopilot_fk_constraint_name('aboxprovenance', 'chunkid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE aboxprovenance DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "aboxprovenance" ALTER COLUMN "chunkid" DROP NOT NULL;
        END IF;
        ALTER TABLE "aboxprovenance" ALTER COLUMN "chunkid" TYPE uuid USING NULL;
        UPDATE "aboxprovenance" a
            SET "chunkid" = c.guid_id
            FROM _fk_snap_abp_chunkid s
            JOIN "chunk" c ON c.id = s.fk_value
            WHERE a.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "aboxprovenance" ALTER COLUMN "chunkid" SET NOT NULL;
        END IF;
        ALTER TABLE "aboxprovenance"
            ADD CONSTRAINT "FK_aboxprovenance_chunk_ChunkId"
            FOREIGN KEY ("chunkid") REFERENCES "chunk"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'aboxprovenance'
      AND column_name = 'jobid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'aboxprovenance'
          AND column_name = 'jobid';
        CREATE TEMP TABLE _fk_snap_abp_jobid ON COMMIT DROP AS
            SELECT id AS row_id, "jobid" AS fk_value FROM "aboxprovenance";
        fk_name := _ontopilot_fk_constraint_name('aboxprovenance', 'jobid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE aboxprovenance DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "aboxprovenance" ALTER COLUMN "jobid" DROP NOT NULL;
        END IF;
        ALTER TABLE "aboxprovenance" ALTER COLUMN "jobid" TYPE uuid USING NULL;
        UPDATE "aboxprovenance" a
            SET "jobid" = e.guid_id
            FROM _fk_snap_abp_jobid s
            JOIN "extractionjob" e ON e.id = s.fk_value
            WHERE a.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "aboxprovenance" ALTER COLUMN "jobid" SET NOT NULL;
        END IF;
        ALTER TABLE "aboxprovenance"
            ADD CONSTRAINT "FK_aboxprovenance_extractionjob_JobId"
            FOREIGN KEY ("jobid") REFERENCES "extractionjob"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'aboxprovenance'
      AND column_name = 'auditeventid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'aboxprovenance'
          AND column_name = 'auditeventid';
        CREATE TEMP TABLE _fk_snap_abp_aeid ON COMMIT DROP AS
            SELECT id AS row_id, "auditeventid" AS fk_value FROM "aboxprovenance";
        fk_name := _ontopilot_fk_constraint_name('aboxprovenance', 'auditeventid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE aboxprovenance DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "aboxprovenance" ALTER COLUMN "auditeventid" DROP NOT NULL;
        END IF;
        ALTER TABLE "aboxprovenance" ALTER COLUMN "auditeventid" TYPE uuid USING NULL;
        UPDATE "aboxprovenance" a
            SET "auditeventid" = ae.guid_id
            FROM _fk_snap_abp_aeid s
            JOIN "auditevent" ae ON ae.id = s.fk_value
            WHERE a.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "aboxprovenance" ALTER COLUMN "auditeventid" SET NOT NULL;
        END IF;
        ALTER TABLE "aboxprovenance"
            ADD CONSTRAINT "FK_aboxprovenance_auditevent_AuditEventId"
            FOREIGN KEY ("auditeventid") REFERENCES "auditevent"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- ontologyrelease.knowledgesystemid, .createdbyid, .reviewedbyid, .publishedbyid
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'ontologyrelease'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'ontologyrelease'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_or_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "ontologyrelease";
        fk_name := _ontopilot_fk_constraint_name('ontologyrelease', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE ontologyrelease DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "ontologyrelease" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "ontologyrelease" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "ontologyrelease" o
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_or_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE o.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "ontologyrelease" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "ontologyrelease"
            ADD CONSTRAINT "FK_ontologyrelease_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'ontologyrelease'
      AND column_name = 'createdbyid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'ontologyrelease'
          AND column_name = 'createdbyid';
        CREATE TEMP TABLE _fk_snap_or_cbyid ON COMMIT DROP AS
            SELECT id AS row_id, "createdbyid" AS fk_value FROM "ontologyrelease";
        fk_name := _ontopilot_fk_constraint_name('ontologyrelease', 'createdbyid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE ontologyrelease DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "ontologyrelease" ALTER COLUMN "createdbyid" DROP NOT NULL;
        END IF;
        ALTER TABLE "ontologyrelease" ALTER COLUMN "createdbyid" TYPE uuid USING NULL;
        UPDATE "ontologyrelease" o
            SET "createdbyid" = u.guid_id
            FROM _fk_snap_or_cbyid s
            JOIN "users" u ON u.id = s.fk_value
            WHERE o.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "ontologyrelease" ALTER COLUMN "createdbyid" SET NOT NULL;
        END IF;
        ALTER TABLE "ontologyrelease"
            ADD CONSTRAINT "FK_ontologyrelease_users_CreatedById"
            FOREIGN KEY ("createdbyid") REFERENCES "users"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'ontologyrelease'
      AND column_name = 'reviewedbyid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'ontologyrelease'
          AND column_name = 'reviewedbyid';
        CREATE TEMP TABLE _fk_snap_or_rbyid ON COMMIT DROP AS
            SELECT id AS row_id, "reviewedbyid" AS fk_value FROM "ontologyrelease";
        fk_name := _ontopilot_fk_constraint_name('ontologyrelease', 'reviewedbyid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE ontologyrelease DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "ontologyrelease" ALTER COLUMN "reviewedbyid" DROP NOT NULL;
        END IF;
        ALTER TABLE "ontologyrelease" ALTER COLUMN "reviewedbyid" TYPE uuid USING NULL;
        UPDATE "ontologyrelease" o
            SET "reviewedbyid" = u.guid_id
            FROM _fk_snap_or_rbyid s
            JOIN "users" u ON u.id = s.fk_value
            WHERE o.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "ontologyrelease" ALTER COLUMN "reviewedbyid" SET NOT NULL;
        END IF;
        ALTER TABLE "ontologyrelease"
            ADD CONSTRAINT "FK_ontologyrelease_users_ReviewedById"
            FOREIGN KEY ("reviewedbyid") REFERENCES "users"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'ontologyrelease'
      AND column_name = 'publishedbyid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'ontologyrelease'
          AND column_name = 'publishedbyid';
        CREATE TEMP TABLE _fk_snap_or_pbyid ON COMMIT DROP AS
            SELECT id AS row_id, "publishedbyid" AS fk_value FROM "ontologyrelease";
        fk_name := _ontopilot_fk_constraint_name('ontologyrelease', 'publishedbyid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE ontologyrelease DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "ontologyrelease" ALTER COLUMN "publishedbyid" DROP NOT NULL;
        END IF;
        ALTER TABLE "ontologyrelease" ALTER COLUMN "publishedbyid" TYPE uuid USING NULL;
        UPDATE "ontologyrelease" o
            SET "publishedbyid" = u.guid_id
            FROM _fk_snap_or_pbyid s
            JOIN "users" u ON u.id = s.fk_value
            WHERE o.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "ontologyrelease" ALTER COLUMN "publishedbyid" SET NOT NULL;
        END IF;
        ALTER TABLE "ontologyrelease"
            ADD CONSTRAINT "FK_ontologyrelease_users_PublishedById"
            FOREIGN KEY ("publishedbyid") REFERENCES "users"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- releasedeployment.knowledgesystemid, .releaseid
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'releasedeployment'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'releasedeployment'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_rd_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "releasedeployment";
        fk_name := _ontopilot_fk_constraint_name('releasedeployment', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE releasedeployment DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "releasedeployment" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "releasedeployment" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "releasedeployment" r
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_rd_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE r.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "releasedeployment" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "releasedeployment"
            ADD CONSTRAINT "FK_releasedeployment_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'releasedeployment'
      AND column_name = 'releaseid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'releasedeployment'
          AND column_name = 'releaseid';
        CREATE TEMP TABLE _fk_snap_rd_relid ON COMMIT DROP AS
            SELECT id AS row_id, "releaseid" AS fk_value FROM "releasedeployment";
        fk_name := _ontopilot_fk_constraint_name('releasedeployment', 'releaseid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE releasedeployment DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "releasedeployment" ALTER COLUMN "releaseid" DROP NOT NULL;
        END IF;
        ALTER TABLE "releasedeployment" ALTER COLUMN "releaseid" TYPE uuid USING NULL;
        UPDATE "releasedeployment" r
            SET "releaseid" = o.guid_id
            FROM _fk_snap_rd_relid s
            JOIN "ontologyrelease" o ON o.id = s.fk_value
            WHERE r.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "releasedeployment" ALTER COLUMN "releaseid" SET NOT NULL;
        END IF;
        ALTER TABLE "releasedeployment"
            ADD CONSTRAINT "FK_releasedeployment_ontologyrelease_ReleaseId"
            FOREIGN KEY ("releaseid") REFERENCES "ontologyrelease"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- releasestatementprovenance.knowledgesystemid, .releaseid
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'releasestatementprovenance'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'releasestatementprovenance'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_rsp_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "releasestatementprovenance";
        fk_name := _ontopilot_fk_constraint_name('releasestatementprovenance', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE releasestatementprovenance DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "releasestatementprovenance" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "releasestatementprovenance" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "releasestatementprovenance" r
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_rsp_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE r.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "releasestatementprovenance" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "releasestatementprovenance"
            ADD CONSTRAINT "FK_rsp_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'releasestatementprovenance'
      AND column_name = 'releaseid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'releasestatementprovenance'
          AND column_name = 'releaseid';
        CREATE TEMP TABLE _fk_snap_rsp_relid ON COMMIT DROP AS
            SELECT id AS row_id, "releaseid" AS fk_value FROM "releasestatementprovenance";
        fk_name := _ontopilot_fk_constraint_name('releasestatementprovenance', 'releaseid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE releasestatementprovenance DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "releasestatementprovenance" ALTER COLUMN "releaseid" DROP NOT NULL;
        END IF;
        ALTER TABLE "releasestatementprovenance" ALTER COLUMN "releaseid" TYPE uuid USING NULL;
        UPDATE "releasestatementprovenance" r
            SET "releaseid" = o.guid_id
            FROM _fk_snap_rsp_relid s
            JOIN "ontologyrelease" o ON o.id = s.fk_value
            WHERE r.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "releasestatementprovenance" ALTER COLUMN "releaseid" SET NOT NULL;
        END IF;
        ALTER TABLE "releasestatementprovenance"
            ADD CONSTRAINT "FK_rsp_ontologyrelease_ReleaseId"
            FOREIGN KEY ("releaseid") REFERENCES "ontologyrelease"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- exportjob.knowledgesystemid, .releaseid, .createdbyid
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'exportjob'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'exportjob'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_xj_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "exportjob";
        fk_name := _ontopilot_fk_constraint_name('exportjob', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE exportjob DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "exportjob" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "exportjob" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "exportjob" e
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_xj_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE e.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "exportjob" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "exportjob"
            ADD CONSTRAINT "FK_exportjob_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'exportjob'
      AND column_name = 'releaseid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'exportjob'
          AND column_name = 'releaseid';
        CREATE TEMP TABLE _fk_snap_xj_relid ON COMMIT DROP AS
            SELECT id AS row_id, "releaseid" AS fk_value FROM "exportjob";
        fk_name := _ontopilot_fk_constraint_name('exportjob', 'releaseid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE exportjob DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "exportjob" ALTER COLUMN "releaseid" DROP NOT NULL;
        END IF;
        ALTER TABLE "exportjob" ALTER COLUMN "releaseid" TYPE uuid USING NULL;
        UPDATE "exportjob" e
            SET "releaseid" = o.guid_id
            FROM _fk_snap_xj_relid s
            JOIN "ontologyrelease" o ON o.id = s.fk_value
            WHERE e.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "exportjob" ALTER COLUMN "releaseid" SET NOT NULL;
        END IF;
        ALTER TABLE "exportjob"
            ADD CONSTRAINT "FK_exportjob_ontologyrelease_ReleaseId"
            FOREIGN KEY ("releaseid") REFERENCES "ontologyrelease"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'exportjob'
      AND column_name = 'createdbyid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'exportjob'
          AND column_name = 'createdbyid';
        CREATE TEMP TABLE _fk_snap_xj_cbyid ON COMMIT DROP AS
            SELECT id AS row_id, "createdbyid" AS fk_value FROM "exportjob";
        fk_name := _ontopilot_fk_constraint_name('exportjob', 'createdbyid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE exportjob DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "exportjob" ALTER COLUMN "createdbyid" DROP NOT NULL;
        END IF;
        ALTER TABLE "exportjob" ALTER COLUMN "createdbyid" TYPE uuid USING NULL;
        UPDATE "exportjob" e
            SET "createdbyid" = u.guid_id
            FROM _fk_snap_xj_cbyid s
            JOIN "users" u ON u.id = s.fk_value
            WHERE e.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "exportjob" ALTER COLUMN "createdbyid" SET NOT NULL;
        END IF;
        ALTER TABLE "exportjob"
            ADD CONSTRAINT "FK_exportjob_users_CreatedById"
            FOREIGN KEY ("createdbyid") REFERENCES "users"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- conflict.knowledgesystemid
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'conflict'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'conflict'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_cf_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "conflict";
        fk_name := _ontopilot_fk_constraint_name('conflict', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE conflict DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "conflict" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "conflict" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "conflict" c
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_cf_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE c.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "conflict" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "conflict"
            ADD CONSTRAINT "FK_conflict_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- entityresolution.knowledgesystemid, .sourcechunkid
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'entityresolution'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'entityresolution'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_er_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "entityresolution";
        fk_name := _ontopilot_fk_constraint_name('entityresolution', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE entityresolution DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "entityresolution" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "entityresolution" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "entityresolution" e
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_er_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE e.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "entityresolution" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "entityresolution"
            ADD CONSTRAINT "FK_entityresolution_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'entityresolution'
      AND column_name = 'sourcechunkid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'entityresolution'
          AND column_name = 'sourcechunkid';
        CREATE TEMP TABLE _fk_snap_er_scid ON COMMIT DROP AS
            SELECT id AS row_id, "sourcechunkid" AS fk_value FROM "entityresolution";
        fk_name := _ontopilot_fk_constraint_name('entityresolution', 'sourcechunkid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE entityresolution DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "entityresolution" ALTER COLUMN "sourcechunkid" DROP NOT NULL;
        END IF;
        ALTER TABLE "entityresolution" ALTER COLUMN "sourcechunkid" TYPE uuid USING NULL;
        UPDATE "entityresolution" e
            SET "sourcechunkid" = c.guid_id
            FROM _fk_snap_er_scid s
            JOIN "chunk" c ON c.id = s.fk_value
            WHERE e.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "entityresolution" ALTER COLUMN "sourcechunkid" SET NOT NULL;
        END IF;
        ALTER TABLE "entityresolution"
            ADD CONSTRAINT "FK_entityresolution_chunk_SourceChunkId"
            FOREIGN KEY ("sourcechunkid") REFERENCES "chunk"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- termproposal.knowledgesystemid, .extractionjobid
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'termproposal'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'termproposal'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_tp_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "termproposal";
        fk_name := _ontopilot_fk_constraint_name('termproposal', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE termproposal DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "termproposal" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "termproposal" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "termproposal" t
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_tp_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE t.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "termproposal" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "termproposal"
            ADD CONSTRAINT "FK_termproposal_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'termproposal'
      AND column_name = 'extractionjobid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'termproposal'
          AND column_name = 'extractionjobid';
        CREATE TEMP TABLE _fk_snap_tp_ejid ON COMMIT DROP AS
            SELECT id AS row_id, "extractionjobid" AS fk_value FROM "termproposal";
        fk_name := _ontopilot_fk_constraint_name('termproposal', 'extractionjobid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE termproposal DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "termproposal" ALTER COLUMN "extractionjobid" DROP NOT NULL;
        END IF;
        ALTER TABLE "termproposal" ALTER COLUMN "extractionjobid" TYPE uuid USING NULL;
        UPDATE "termproposal" t
            SET "extractionjobid" = e.guid_id
            FROM _fk_snap_tp_ejid s
            JOIN "extractionjob" e ON e.id = s.fk_value
            WHERE t.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "termproposal" ALTER COLUMN "extractionjobid" SET NOT NULL;
        END IF;
        ALTER TABLE "termproposal"
            ADD CONSTRAINT "FK_termproposal_extractionjob_ExtractionJobId"
            FOREIGN KEY ("extractionjobid") REFERENCES "extractionjob"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- tboxreconciliation.knowledgesystemid
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'tboxreconciliation'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'tboxreconciliation'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_tbr_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "tboxreconciliation";
        fk_name := _ontopilot_fk_constraint_name('tboxreconciliation', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE tboxreconciliation DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "tboxreconciliation" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "tboxreconciliation" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "tboxreconciliation" t
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_tbr_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE t.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "tboxreconciliation" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "tboxreconciliation"
            ADD CONSTRAINT "FK_tboxreconciliation_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- validationdecision.knowledgesystemid
-- ---------------------------------------------------------------------------
DO $$
DECLARE col_type text; fk_name text; was_not_null boolean;
BEGIN
    SELECT data_type INTO col_type
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'validationdecision'
      AND column_name = 'knowledgesystemid';
    IF col_type = 'bigint' THEN
        SELECT is_nullable = 'NO' INTO was_not_null
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'validationdecision'
          AND column_name = 'knowledgesystemid';
        CREATE TEMP TABLE _fk_snap_vd_ksid ON COMMIT DROP AS
            SELECT id AS row_id, "knowledgesystemid" AS fk_value FROM "validationdecision";
        fk_name := _ontopilot_fk_constraint_name('validationdecision', 'knowledgesystemid');
        IF fk_name IS NOT NULL THEN
            EXECUTE format('ALTER TABLE validationdecision DROP CONSTRAINT %I', fk_name);
        END IF;
        IF was_not_null THEN
            ALTER TABLE "validationdecision" ALTER COLUMN "knowledgesystemid" DROP NOT NULL;
        END IF;
        ALTER TABLE "validationdecision" ALTER COLUMN "knowledgesystemid" TYPE uuid USING NULL;
        UPDATE "validationdecision" v
            SET "knowledgesystemid" = ks.guid_id
            FROM _fk_snap_vd_ksid s
            JOIN "knowledgesystem" ks ON ks.id = s.fk_value
            WHERE v.id = s.row_id;
        IF was_not_null THEN
            ALTER TABLE "validationdecision" ALTER COLUMN "knowledgesystemid" SET NOT NULL;
        END IF;
        ALTER TABLE "validationdecision"
            ADD CONSTRAINT "FK_validationdecision_knowledgesystem_KnowledgeSystemId"
            FOREIGN KEY ("knowledgesystemid") REFERENCES "knowledgesystem"("guid_id") ON DELETE RESTRICT;
    END IF;
END $$;
