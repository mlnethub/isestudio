-- =============================================================================
-- 001_add_guid_and_legacy_ids.sql
--
-- Adds the two new identity columns required by the .NET / EF Core schema:
--   * guid_id  uuid       : the new primary key (replaces the Python bigint id)
--   * legacy_id bigint    : a stable copy of the original bigint id, used as
--                           the compat key by every REST route that still
--                           references the legacy integer id
--
-- Every statement is idempotent (ADD COLUMN IF NOT EXISTS, UPDATE guarded by
-- IS NULL, CREATE UNIQUE INDEX IF NOT EXISTS) so re-running the migration
-- is a no-op.
--
-- The original bigint id column is left untouched. The primary key constraint
-- on it is dropped by 003_apply_ef_constraints.sql after the new guid_id
-- is fully populated and indexed.
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- ---------------------------------------------------------------------------
-- users (no FKs, root table)
-- ---------------------------------------------------------------------------
ALTER TABLE "users"      ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "users"      ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "users"           SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "users"           SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_users_guid_id ON "users"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_users_legacy_id ON "users"(legacy_id);

-- ---------------------------------------------------------------------------
-- provider (no FKs, root table)
-- ---------------------------------------------------------------------------
ALTER TABLE "provider"   ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "provider"   ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "provider"        SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "provider"        SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_provider_guid_id ON "provider"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_provider_legacy_id ON "provider"(legacy_id);

-- ---------------------------------------------------------------------------
-- knowledgesystem (FKs: ownerid -> users, llmproviderid/embeddingproviderid -> provider)
-- ---------------------------------------------------------------------------
ALTER TABLE "knowledgesystem" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "knowledgesystem" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "knowledgesystem"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "knowledgesystem"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_knowledgesystem_guid_id ON "knowledgesystem"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_knowledgesystem_legacy_id ON "knowledgesystem"(legacy_id);

-- ---------------------------------------------------------------------------
-- authsession (FK: userid -> users)
-- ---------------------------------------------------------------------------
ALTER TABLE "authsession" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "authsession" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "authsession"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "authsession"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_authsession_guid_id ON "authsession"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_authsession_legacy_id ON "authsession"(legacy_id);

-- ---------------------------------------------------------------------------
-- ksgrant (FK: knowledgesystemid -> knowledgesystem, userid -> users)
-- ---------------------------------------------------------------------------
ALTER TABLE "ksgrant"   ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "ksgrant"   ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "ksgrant"        SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "ksgrant"        SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_ksgrant_guid_id ON "ksgrant"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_ksgrant_legacy_id ON "ksgrant"(legacy_id);

-- ---------------------------------------------------------------------------
-- knowledgepromptoverride (FK: knowledgesystemid -> knowledgesystem, updatedbyid -> users)
-- ---------------------------------------------------------------------------
ALTER TABLE "knowledgepromptoverride" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "knowledgepromptoverride" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "knowledgepromptoverride"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "knowledgepromptoverride"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_kpo_guid_id ON "knowledgepromptoverride"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_kpo_legacy_id ON "knowledgepromptoverride"(legacy_id);

-- ---------------------------------------------------------------------------
-- knowledgeapitoken (FK: knowledgesystemid -> knowledgesystem, createdbyid -> users)
-- ---------------------------------------------------------------------------
ALTER TABLE "knowledgeapitoken" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "knowledgeapitoken" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "knowledgeapitoken"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "knowledgeapitoken"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_kat_guid_id ON "knowledgeapitoken"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_kat_legacy_id ON "knowledgeapitoken"(legacy_id);

-- ---------------------------------------------------------------------------
-- mcpusertoken (FK: knowledgesystemid -> knowledgesystem, userid -> users)
-- ---------------------------------------------------------------------------
ALTER TABLE "mcpusertoken" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "mcpusertoken" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "mcpusertoken"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "mcpusertoken"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_mcp_guid_id ON "mcpusertoken"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mcp_legacy_id ON "mcpusertoken"(legacy_id);

-- ---------------------------------------------------------------------------
-- document (FK: knowledgesystemid -> knowledgesystem)
-- ---------------------------------------------------------------------------
ALTER TABLE "document"   ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "document"   ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "document"        SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "document"        SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_document_guid_id ON "document"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_document_legacy_id ON "document"(legacy_id);

-- ---------------------------------------------------------------------------
-- chunk (FK: documentid -> document)
-- ---------------------------------------------------------------------------
ALTER TABLE "chunk"      ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "chunk"      ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "chunk"           SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "chunk"           SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_chunk_guid_id ON "chunk"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_chunk_legacy_id ON "chunk"(legacy_id);

-- ---------------------------------------------------------------------------
-- systemconfig (FK: llmproviderid/embeddingproviderid -> provider)
-- ---------------------------------------------------------------------------
ALTER TABLE "systemconfig" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "systemconfig" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "systemconfig"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "systemconfig"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_systemconfig_guid_id ON "systemconfig"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_systemconfig_legacy_id ON "systemconfig"(legacy_id);

-- ---------------------------------------------------------------------------
-- extractionjob (FK: knowledgesystemid -> knowledgesystem)
-- ---------------------------------------------------------------------------
ALTER TABLE "extractionjob" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "extractionjob" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "extractionjob"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "extractionjob"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_extractionjob_guid_id ON "extractionjob"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_extractionjob_legacy_id ON "extractionjob"(legacy_id);

-- ---------------------------------------------------------------------------
-- auditevent (FK: knowledgesystemid -> knowledgesystem, actorid -> users)
-- ---------------------------------------------------------------------------
ALTER TABLE "auditevent" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "auditevent" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "auditevent"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "auditevent"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_auditevent_guid_id ON "auditevent"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_auditevent_legacy_id ON "auditevent"(legacy_id);

-- ---------------------------------------------------------------------------
-- axiomprovenance (FK: knowledgesystemid -> knowledgesystem,
--                  chunkid -> chunk, jobid -> extractionjob,
--                  auditeventid -> auditevent)
-- ---------------------------------------------------------------------------
ALTER TABLE "axiomprovenance" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "axiomprovenance" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "axiomprovenance"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "axiomprovenance"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_axiomprovenance_guid_id ON "axiomprovenance"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_axiomprovenance_legacy_id ON "axiomprovenance"(legacy_id);

-- ---------------------------------------------------------------------------
-- aboxprovenance (FK: knowledgesystemid -> knowledgesystem,
--                 chunkid -> chunk, jobid -> extractionjob,
--                 auditeventid -> auditevent)
-- ---------------------------------------------------------------------------
ALTER TABLE "aboxprovenance" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "aboxprovenance" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "aboxprovenance"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "aboxprovenance"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_aboxprovenance_guid_id ON "aboxprovenance"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_aboxprovenance_legacy_id ON "aboxprovenance"(legacy_id);

-- ---------------------------------------------------------------------------
-- ontologyrelease (FK: knowledgesystemid -> knowledgesystem,
--                  createdbyid/reviewedbyid/publishedbyid -> users)
-- ---------------------------------------------------------------------------
ALTER TABLE "ontologyrelease" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "ontologyrelease" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "ontologyrelease"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "ontologyrelease"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_ontologyrelease_guid_id ON "ontologyrelease"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_ontologyrelease_legacy_id ON "ontologyrelease"(legacy_id);

-- ---------------------------------------------------------------------------
-- releasedeployment (FK: knowledgesystemid -> knowledgesystem, releaseid -> ontologyrelease)
-- ---------------------------------------------------------------------------
ALTER TABLE "releasedeployment" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "releasedeployment" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "releasedeployment"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "releasedeployment"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_releasedeployment_guid_id ON "releasedeployment"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_releasedeployment_legacy_id ON "releasedeployment"(legacy_id);

-- ---------------------------------------------------------------------------
-- releasestatementprovenance (FK: knowledgesystemid -> knowledgesystem, releaseid -> ontologyrelease)
-- ---------------------------------------------------------------------------
ALTER TABLE "releasestatementprovenance" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "releasestatementprovenance" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "releasestatementprovenance"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "releasestatementprovenance"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_rsp_guid_id ON "releasestatementprovenance"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_rsp_legacy_id ON "releasestatementprovenance"(legacy_id);

-- ---------------------------------------------------------------------------
-- exportjob (FK: knowledgesystemid -> knowledgesystem, releaseid -> ontologyrelease,
--            createdbyid -> users)
-- ---------------------------------------------------------------------------
ALTER TABLE "exportjob" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "exportjob" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "exportjob"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "exportjob"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_exportjob_guid_id ON "exportjob"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_exportjob_legacy_id ON "exportjob"(legacy_id);

-- ---------------------------------------------------------------------------
-- conflict (FK: knowledgesystemid -> knowledgesystem)
-- ---------------------------------------------------------------------------
ALTER TABLE "conflict"  ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "conflict"  ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "conflict"       SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "conflict"       SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_conflict_guid_id ON "conflict"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_conflict_legacy_id ON "conflict"(legacy_id);

-- ---------------------------------------------------------------------------
-- entityresolution (FK: knowledgesystemid -> knowledgesystem, sourcechunkid -> chunk)
-- ---------------------------------------------------------------------------
ALTER TABLE "entityresolution" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "entityresolution" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "entityresolution"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "entityresolution"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_entityresolution_guid_id ON "entityresolution"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_entityresolution_legacy_id ON "entityresolution"(legacy_id);

-- ---------------------------------------------------------------------------
-- termproposal (FK: knowledgesystemid -> knowledgesystem, extractionjobid -> extractionjob)
-- ---------------------------------------------------------------------------
ALTER TABLE "termproposal" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "termproposal" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "termproposal"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "termproposal"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_termproposal_guid_id ON "termproposal"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_termproposal_legacy_id ON "termproposal"(legacy_id);

-- ---------------------------------------------------------------------------
-- tboxreconciliation (FK: knowledgesystemid -> knowledgesystem)
-- ---------------------------------------------------------------------------
ALTER TABLE "tboxreconciliation" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "tboxreconciliation" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "tboxreconciliation"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "tboxreconciliation"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_tboxreconciliation_guid_id ON "tboxreconciliation"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_tboxreconciliation_legacy_id ON "tboxreconciliation"(legacy_id);

-- ---------------------------------------------------------------------------
-- validationdecision (FK: knowledgesystemid -> knowledgesystem)
-- ---------------------------------------------------------------------------
ALTER TABLE "validationdecision" ADD COLUMN IF NOT EXISTS guid_id  uuid;
ALTER TABLE "validationdecision" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "validationdecision"      SET guid_id   = gen_random_uuid() WHERE guid_id   IS NULL;
UPDATE "validationdecision"      SET legacy_id = id                  WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_validationdecision_guid_id ON "validationdecision"(guid_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_validationdecision_legacy_id ON "validationdecision"(legacy_id);
