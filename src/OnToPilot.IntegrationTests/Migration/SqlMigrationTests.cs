using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OnToPilot.Migration.Sql;
using Testcontainers.PostgreSql;

namespace OnToPilot.IntegrationTests.Migration;

/// <summary>
/// Verifies the reversible SQL migration that bridges the Python / SQLAlchemy
/// schema (every business table keyed by a bigint <c>id</c> primary key) into
/// the .NET / EF Core schema (every business table keyed by a <c>guid_id</c>
/// UUID, with a stable <c>legacy_id</c> bigint compatibility column).
///
/// <para>Each test class owns a unique Testcontainers-managed Postgres so
/// concurrent runs don't share state. Docker must be available; the fixture
/// throws if it cannot start a container, mirroring the
/// <c>PostgresSchemaTests</c> convention.</para>
///
/// <para>All tests carry <c>[Trait("Category", "Migration")]</c> so the
/// rehearsal / cutover orchestration (Task 4) can filter them out of the
/// default CI run.</para>
/// </summary>
public sealed class SqlMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlBuilder _builder = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("ontopilot")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true);

    private PostgreSqlContainer _container = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _container = _builder.Build();
        await _container.StartAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    /// The Python / SQLAlchemy schema seeds the 24 business tables with
    /// bigint <c>id</c> primary keys and bigint foreign-key columns. This
    /// helper creates that shape and returns the connection string.
    /// </summary>
    private async Task SeedPythonSchemaAsync(NpgsqlConnectionStringBuilder csb)
    {
        await using var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = PythonSchemaScript;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = PythonSeedScript;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// The 24 business tables with Python / SQLAlchemy column types. Every
    /// table has a bigint <c>id</c> primary key. Foreign-key columns are
    /// bigint referencing the parent table's <c>id</c>.
    /// </summary>
    private const string PythonSchemaScript = @"
        CREATE TABLE users (
            id bigserial PRIMARY KEY,
            username varchar(255) NOT NULL UNIQUE,
            displayname varchar(255),
            passwordhash varchar(255) NOT NULL,
            isadmin boolean NOT NULL DEFAULT false,
            active boolean NOT NULL DEFAULT true,
            createdat timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE provider (
            id bigserial PRIMARY KEY,
            name varchar(255) NOT NULL,
            baseurl varchar(2048) NOT NULL DEFAULT '',
            apikey varchar(2048) NOT NULL DEFAULT '',
            model varchar(255) NOT NULL DEFAULT '',
            kind varchar(32) NOT NULL DEFAULT 'llm',
            concurrencylimit integer NOT NULL DEFAULT 10,
            lasttestok boolean,
            lasttestedat timestamptz,
            createdat timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE knowledgesystem (
            id bigserial PRIMARY KEY,
            publicid varchar(64) NOT NULL UNIQUE,
            name varchar(255) NOT NULL,
            description text NOT NULL DEFAULT '',
            ownerid bigint REFERENCES users(id) ON DELETE RESTRICT,
            graphiri varchar(1024) NOT NULL,
            baseiri varchar(1024) NOT NULL,
            createdat timestamptz NOT NULL DEFAULT now(),
            updatedat timestamptz NOT NULL DEFAULT now(),
            classcount integer NOT NULL DEFAULT 0,
            propertycount integer NOT NULL DEFAULT 0,
            axiomcount integer NOT NULL DEFAULT 0,
            llmmodel varchar(255),
            llmproviderid bigint REFERENCES provider(id) ON DELETE RESTRICT,
            embeddingproviderid bigint REFERENCES provider(id) ON DELETE RESTRICT,
            embeddingmodel varchar(255)
        );
        CREATE TABLE authsession (
            id bigserial PRIMARY KEY,
            token varchar(255) NOT NULL UNIQUE,
            userid bigint NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
            createdat timestamptz NOT NULL DEFAULT now(),
            expiresat timestamptz NOT NULL
        );
        CREATE TABLE ksgrant (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            userid bigint NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
            role varchar(32) NOT NULL DEFAULT 'viewer',
            createdat timestamptz NOT NULL DEFAULT now(),
            UNIQUE (knowledgesystemid, userid)
        );
        CREATE TABLE knowledgepromptoverride (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            promptkey varchar(255) NOT NULL,
            content text NOT NULL,
            updatedbyid bigint REFERENCES users(id) ON DELETE RESTRICT,
            updatedbyname varchar(255) NOT NULL DEFAULT '',
            createdat timestamptz NOT NULL DEFAULT now(),
            updatedat timestamptz NOT NULL DEFAULT now(),
            UNIQUE (knowledgesystemid, promptkey)
        );
        CREATE TABLE knowledgeapitoken (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            name varchar(255) NOT NULL,
            tokenprefix varchar(64) NOT NULL,
            tokenhash varchar(128) NOT NULL UNIQUE,
            secretciphertext text,
            scopes jsonb NOT NULL DEFAULT '[]',
            createdbyid bigint REFERENCES users(id) ON DELETE RESTRICT,
            createdat timestamptz NOT NULL DEFAULT now(),
            expiresat timestamptz,
            lastusedat timestamptz,
            revokedat timestamptz
        );
        CREATE TABLE mcpusertoken (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            userid bigint NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
            name varchar(255) NOT NULL,
            tokenprefix varchar(64) NOT NULL,
            tokenhash varchar(128) NOT NULL UNIQUE,
            scopes jsonb NOT NULL DEFAULT '[]',
            createdat timestamptz NOT NULL DEFAULT now(),
            expiresat timestamptz NOT NULL,
            lastusedat timestamptz,
            revokedat timestamptz
        );
        CREATE TABLE document (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            sha256 varchar(64) NOT NULL,
            originalfilename varchar(512) NOT NULL DEFAULT '',
            folder varchar(1024) NOT NULL DEFAULT '/',
            ext varchar(32) NOT NULL DEFAULT '',
            mime varchar(255),
            sizebytes bigint NOT NULL DEFAULT 0,
            storagepath varchar(1024) NOT NULL DEFAULT '',
            uploadedat timestamptz NOT NULL DEFAULT now(),
            parsestatus varchar(32) NOT NULL DEFAULT 'pending',
            parserbackend varchar(64),
            parseerror text,
            textcharcount integer,
            chunkcount integer NOT NULL DEFAULT 0,
            tboxextractedat timestamptz,
            aboxextractedat timestamptz,
            UNIQUE (knowledgesystemid, sha256)
        );
        CREATE TABLE chunk (
            id bigserial PRIMARY KEY,
            documentid bigint NOT NULL REFERENCES document(id) ON DELETE CASCADE,
            idx integer NOT NULL,
            text text NOT NULL,
            charstart integer NOT NULL,
            charend integer NOT NULL,
            tokenestimate integer NOT NULL DEFAULT 0,
            createdat timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE systemconfig (
            id bigserial PRIMARY KEY,
            extractmodel varchar(255),
            embeddingmodel varchar(255),
            llmproviderid bigint REFERENCES provider(id) ON DELETE RESTRICT,
            embeddingproviderid bigint REFERENCES provider(id) ON DELETE RESTRICT,
            extractionconcurrency integer,
            baseurl varchar(2048),
            apikey varchar(2048),
            updatedat timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE extractionjob (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            kind varchar(32) NOT NULL DEFAULT 'tbox',
            status varchar(32) NOT NULL DEFAULT 'pending',
            model varchar(255) NOT NULL DEFAULT '',
            promptsnapshot jsonb,
            chunkids jsonb NOT NULL DEFAULT '[]',
            createdat timestamptz NOT NULL DEFAULT now(),
            finishedat timestamptz,
            log text NOT NULL DEFAULT '',
            error text,
            totalchunks integer NOT NULL DEFAULT 0,
            processedchunks integer NOT NULL DEFAULT 0,
            classesadded integer NOT NULL DEFAULT 0,
            propertiesadded integer NOT NULL DEFAULT 0,
            axiomsadded integer NOT NULL DEFAULT 0,
            individualsadded integer NOT NULL DEFAULT 0,
            assertionsadded integer NOT NULL DEFAULT 0,
            pendingadded integer NOT NULL DEFAULT 0,
            unknownclasses jsonb,
            phase varchar(32) NOT NULL DEFAULT '',
            termsadded integer NOT NULL DEFAULT 0,
            termsmapped integer NOT NULL DEFAULT 0,
            terminologyproposals integer NOT NULL DEFAULT 0,
            terminologyerror text
        );
        CREATE TABLE auditevent (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            actorid bigint REFERENCES users(id) ON DELETE RESTRICT,
            actorname varchar(255) NOT NULL DEFAULT '',
            action varchar(128) NOT NULL DEFAULT '',
            summary varchar(1024) NOT NULL DEFAULT '',
            detail jsonb,
            graph varchar(1024),
            groupid varchar(64),
            added bytea,
            removed bytea,
            createdat timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE axiomprovenance (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            axiomkey varchar(512) NOT NULL,
            chunkid bigint REFERENCES chunk(id) ON DELETE RESTRICT,
            jobid bigint REFERENCES extractionjob(id) ON DELETE RESTRICT,
            method varchar(64) NOT NULL DEFAULT 'extraction',
            actorname varchar(255) NOT NULL DEFAULT '',
            auditeventid bigint REFERENCES auditevent(id) ON DELETE RESTRICT,
            reviewrecord jsonb,
            createdat timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE aboxprovenance (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            factkey varchar(512) NOT NULL,
            chunkid bigint REFERENCES chunk(id) ON DELETE RESTRICT,
            jobid bigint REFERENCES extractionjob(id) ON DELETE RESTRICT,
            method varchar(64) NOT NULL DEFAULT 'extraction',
            actorname varchar(255) NOT NULL DEFAULT '',
            auditeventid bigint REFERENCES auditevent(id) ON DELETE RESTRICT,
            reviewrecord jsonb,
            createdat timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE ontologyrelease (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            version varchar(64) NOT NULL,
            status varchar(32) NOT NULL DEFAULT 'draft',
            title varchar(255) NOT NULL DEFAULT '',
            notes text NOT NULL DEFAULT '',
            snapshotdir varchar(1024) NOT NULL DEFAULT '',
            manifest jsonb,
            createdbyid bigint REFERENCES users(id) ON DELETE RESTRICT,
            createdbyname varchar(255) NOT NULL DEFAULT '',
            reviewedbyid bigint REFERENCES users(id) ON DELETE RESTRICT,
            reviewedbyname varchar(255) NOT NULL DEFAULT '',
            publishedbyid bigint REFERENCES users(id) ON DELETE RESTRICT,
            publishedbyname varchar(255) NOT NULL DEFAULT '',
            createdat timestamptz NOT NULL DEFAULT now(),
            reviewedat timestamptz,
            publishedat timestamptz,
            UNIQUE (knowledgesystemid, version)
        );
        CREATE TABLE releasedeployment (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            releaseid bigint NOT NULL UNIQUE REFERENCES ontologyrelease(id) ON DELETE RESTRICT,
            status varchar(32) NOT NULL DEFAULT 'provisioning',
            tboxgraphiri varchar(1024) NOT NULL DEFAULT '',
            vocabularygraphiri varchar(1024) NOT NULL DEFAULT '',
            aboxgraphiri varchar(1024) NOT NULL DEFAULT '',
            statementcount integer NOT NULL DEFAULT 0,
            provenancecount integer NOT NULL DEFAULT 0,
            error text,
            createdat timestamptz NOT NULL DEFAULT now(),
            activatedat timestamptz,
            stoppedat timestamptz
        );
        CREATE TABLE releasestatementprovenance (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            releaseid bigint NOT NULL REFERENCES ontologyrelease(id) ON DELETE RESTRICT,
            layer varchar(32) NOT NULL,
            statementkey varchar(512) NOT NULL,
            payload jsonb
        );
        CREATE TABLE exportjob (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            releaseid bigint REFERENCES ontologyrelease(id) ON DELETE RESTRICT,
            layer varchar(32) NOT NULL,
            format varchar(32) NOT NULL DEFAULT 'nquads',
            status varchar(32) NOT NULL DEFAULT 'pending',
            shardsize integer NOT NULL DEFAULT 100000,
            processedstatements integer NOT NULL DEFAULT 0,
            totalstatements integer NOT NULL DEFAULT 0,
            outputdir varchar(1024) NOT NULL DEFAULT '',
            files jsonb,
            error text,
            createdbyid bigint REFERENCES users(id) ON DELETE RESTRICT,
            createdbyname varchar(255) NOT NULL DEFAULT '',
            createdat timestamptz NOT NULL DEFAULT now(),
            startedat timestamptz,
            finishedat timestamptz
        );
        CREATE TABLE conflict (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            signature varchar(255) NOT NULL,
            ctype varchar(64) NOT NULL,
            severity varchar(32) NOT NULL DEFAULT 'error',
            status varchar(32) NOT NULL DEFAULT 'open',
            title varchar(255) NOT NULL DEFAULT '',
            detail text NOT NULL DEFAULT '',
            payload jsonb,
            createdat timestamptz NOT NULL DEFAULT now(),
            resolvedat timestamptz,
            resolution varchar(64)
        );
        CREATE TABLE entityresolution (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            surfaceform varchar(512) NOT NULL,
            classiri varchar(1024),
            status varchar(32) NOT NULL DEFAULT 'pending',
            individualiri varchar(1024),
            confidence double precision,
            resolvedby varchar(64),
            sourcechunkid bigint REFERENCES chunk(id) ON DELETE RESTRICT,
            context jsonb,
            createdat timestamptz NOT NULL DEFAULT now(),
            resolvedat timestamptz
        );
        CREATE TABLE termproposal (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            signature varchar(255) NOT NULL,
            action varchar(32) NOT NULL,
            term varchar(255) NOT NULL,
            targetiri varchar(1024),
            status varchar(32) NOT NULL DEFAULT 'pending',
            payload jsonb,
            confidence double precision,
            reason text,
            evidence jsonb,
            sourcechunkids jsonb,
            extractionjobid bigint REFERENCES extractionjob(id) ON DELETE RESTRICT,
            proposedby varchar(64) NOT NULL DEFAULT 'terminology-agent',
            resolvedby varchar(64),
            resolutionnote text,
            createdat timestamptz NOT NULL DEFAULT now(),
            resolvedat timestamptz
        );
        CREATE TABLE tboxreconciliation (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            slot varchar(32) NOT NULL,
            propertylabel varchar(255) NOT NULL,
            propertyiri varchar(1024),
            candidates jsonb,
            choice varchar(64) NOT NULL,
            chosenlabel varchar(255),
            reason text,
            resolvedby varchar(64),
            createdat timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE validationdecision (
            id bigserial PRIMARY KEY,
            knowledgesystemid bigint NOT NULL REFERENCES knowledgesystem(id) ON DELETE RESTRICT,
            propertylabel varchar(255) NOT NULL,
            propertyiri varchar(1024),
            xsdtype varchar(64),
            action varchar(32) NOT NULL,
            reason text,
            resolvedby varchar(64),
            createdat timestamptz NOT NULL DEFAULT now()
        );";

    /// <summary>
    /// Deterministic seed: a tiny graph of users, providers, knowledge
    /// systems, sessions, and per-row payloads (JSON, bytea) that exercise
    /// every column type, every business checksum, and every FK direction.
    /// </summary>
    private const string PythonSeedScript = @"
        -- 2 providers
        INSERT INTO provider (id, name, baseurl, apikey, model, kind, concurrencylimit, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (1, 'openai-main', 'https://api.example.com', 'sk-test', 'gpt-4o', 'llm', 8, '2026-01-01T00:00:00Z'),
                   (2, 'embed-mini', 'https://api.example.com', 'sk-test', 'text-embed', 'embedding', 4, '2026-01-01T00:00:00Z');
        -- 2 users
        INSERT INTO users (id, username, displayname, passwordhash, isadmin, active, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (10, 'admin', 'Admin', 'hash-admin', true, true, '2026-01-01T00:00:00Z'),
                   (11, 'editor', 'Editor', 'hash-editor', false, true, '2026-01-01T00:00:00Z');
        -- 2 knowledge systems
        INSERT INTO knowledgesystem (id, publicid, name, description, ownerid, graphiri, baseiri, createdat, updatedat, classcount, propertycount, axiomcount, llmproviderid, embeddingproviderid)
            OVERRIDING SYSTEM VALUE
            VALUES (100, 'ks-1', 'KS One', 'first ks', 10, 'http://ontopilot.local/ks/100', 'http://ontopilot.local/ks/100/onto#', '2026-01-02T00:00:00Z', '2026-01-02T00:00:00Z', 0, 0, 0, 1, 2),
                   (101, 'ks-2', 'KS Two', 'second ks', 11, 'http://ontopilot.local/ks/101', 'http://ontopilot.local/ks/101/onto#', '2026-01-02T00:00:00Z', '2026-01-02T00:00:00Z', 0, 0, 0, 1, 2);
        -- 2 sessions
        INSERT INTO authsession (id, token, userid, createdat, expiresat)
            OVERRIDING SYSTEM VALUE
            VALUES (1000, 'token-A', 10, '2026-01-03T00:00:00Z', '2026-12-31T00:00:00Z'),
                   (1001, 'token-B', 11, '2026-01-03T00:00:00Z', '2026-12-31T00:00:00Z');
        -- ksgrant
        INSERT INTO ksgrant (id, knowledgesystemid, userid, role, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (2000, 100, 11, 'editor', '2026-01-04T00:00:00Z');
        -- prompt overrides
        INSERT INTO knowledgepromptoverride (id, knowledgesystemid, promptkey, content, updatedbyid, updatedbyname, createdat, updatedat)
            OVERRIDING SYSTEM VALUE
            VALUES (3000, 100, 'tbox.system', 'You are a TBox agent.', 10, 'Admin', '2026-01-05T00:00:00Z', '2026-01-05T00:00:00Z');
        -- API tokens
        INSERT INTO knowledgeapitoken (id, knowledgesystemid, name, tokenprefix, tokenhash, scopes, createdbyid, createdat, expiresat)
            OVERRIDING SYSTEM VALUE
            VALUES (4000, 100, 'reader', 'r_', 'h-kat-1', '[""read""]', 10, '2026-01-06T00:00:00Z', null);
        -- MCP tokens
        INSERT INTO mcpusertoken (id, knowledgesystemid, userid, name, tokenprefix, tokenhash, scopes, createdat, expiresat)
            OVERRIDING SYSTEM VALUE
            VALUES (5000, 100, 11, 'mcp-ed', 'm_', 'h-mcp-1', '[""read""]', '2026-01-07T00:00:00Z', '2026-12-31T00:00:00Z');
        -- documents
        INSERT INTO document (id, knowledgesystemid, sha256, originalfilename, folder, ext, mime, sizebytes, storagepath, uploadedat, parsestatus, parserbackend, textcharcount, chunkcount)
            OVERRIDING SYSTEM VALUE
            VALUES (6000, 100, 'sha-doc-1', 'doc.txt', '/', 'txt', 'text/plain', 42, 'aa/bb/sha-doc-1', '2026-01-08T00:00:00Z', 'parsed', 'fallback:txt', 42, 1);
        -- chunks
        INSERT INTO chunk (id, documentid, idx, text, charstart, charend, tokenestimate, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (7000, 6000, 0, 'hello world', 0, 11, 2, '2026-01-08T01:00:00Z');
        -- systemconfig
        INSERT INTO systemconfig (id, extractmodel, embeddingmodel, llmproviderid, embeddingproviderid, updatedat)
            OVERRIDING SYSTEM VALUE
            VALUES (8000, 'gpt-4o', 'text-embed', 1, 2, '2026-01-09T00:00:00Z');
        -- extraction job
        INSERT INTO extractionjob (id, knowledgesystemid, kind, status, model, promptsnapshot, chunkids, createdat, totalchunks, phase, termsadded, termsmapped, terminologyproposals)
            OVERRIDING SYSTEM VALUE
            VALUES (9000, 100, 'tbox', 'completed', 'gpt-4o', '{""foo"": 1}', '[7000]', '2026-01-10T00:00:00Z', 1, 'finalizing', 0, 0, 0);
        -- audit event with jsonb detail + bytea payloads
        INSERT INTO auditevent (id, knowledgesystemid, actorid, actorname, action, summary, detail, added, removed, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (10000, 100, 10, 'Admin', 'ontology.edit', 'created Class X', '{""k"": ""v""}', decode('48656c6c6f', 'hex'), decode('476f6f64627965', 'hex'), '2026-01-11T00:00:00Z');
        -- axiom provenance
        INSERT INTO axiomprovenance (id, knowledgesystemid, axiomkey, chunkid, jobid, method, actorname, auditeventid, reviewrecord, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (11000, 100, 'subClassOf|dog|animal', 7000, 9000, 'extraction', 'Admin', 10000, '{""a"": 1}', '2026-01-12T00:00:00Z');
        -- abox provenance
        INSERT INTO aboxprovenance (id, knowledgesystemid, factkey, chunkid, jobid, method, actorname, auditeventid, reviewrecord, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (12000, 100, 'ind|rex', 7000, 9000, 'extraction', 'Admin', 10000, '{""a"": 1}', '2026-01-12T00:00:00Z');
        -- ontology release
        INSERT INTO ontologyrelease (id, knowledgesystemid, version, status, title, notes, snapshotdir, manifest, createdbyid, createdbyname, reviewedbyid, reviewedbyname, publishedbyid, publishedbyname, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (13000, 100, '1.0.0', 'published', 'v1', 'first release', '/tmp/rel-1', '{""iri"": ""x""}', 10, 'Admin', 10, 'Admin', 10, 'Admin', '2026-01-13T00:00:00Z');
        -- release deployment
        INSERT INTO releasedeployment (id, knowledgesystemid, releaseid, status, tboxgraphiri, vocabularygraphiri, aboxgraphiri, statementcount, provenancecount, createdat, activatedat)
            OVERRIDING SYSTEM VALUE
            VALUES (14000, 100, 13000, 'active', 'g-tbox', 'g-vocab', 'g-abox', 0, 0, '2026-01-14T00:00:00Z', '2026-01-14T00:00:00Z');
        -- release statement provenance
        INSERT INTO releasestatementprovenance (id, knowledgesystemid, releaseid, layer, statementkey, payload)
            OVERRIDING SYSTEM VALUE
            VALUES (15000, 100, 13000, 'tbox', 'subClassOf|dog|animal', '{""p"": 1}');
        -- export job
        INSERT INTO exportjob (id, knowledgesystemid, releaseid, layer, format, status, shardsize, processedstatements, totalstatements, outputdir, files, createdbyid, createdbyname, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (16000, 100, 13000, 'tbox', 'nquads', 'completed', 100, 0, 0, '/tmp/exp-1', '[]', 10, 'Admin', '2026-01-15T00:00:00Z');
        -- conflict
        INSERT INTO conflict (id, knowledgesystemid, signature, ctype, severity, status, title, detail, payload, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (17000, 100, 'sig-c-1', 'cycle', 'error', 'open', 'A is parent of B', 'cycle detail', '{""x"": 1}', '2026-01-16T00:00:00Z');
        -- entity resolution
        INSERT INTO entityresolution (id, knowledgesystemid, surfaceform, classiri, status, individualiri, confidence, resolvedby, sourcechunkid, context, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (18000, 100, 'rex', 'http://ontopilot.local/ks/100/onto#Dog', 'matched', 'http://ontopilot.local/ks/100/ind/rex', 0.95, 'agent', 7000, '{""why"": ""high conf""}', '2026-01-17T00:00:00Z');
        -- term proposal
        INSERT INTO termproposal (id, knowledgesystemid, signature, action, term, targetiri, status, payload, confidence, reason, evidence, sourcechunkids, extractionjobid, proposedby, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (19000, 100, 'sig-t-1', 'create', 'Canine', null, 'pending', '{""d"": ""def""}', 0.7, 'reason text', '{""e"": 1}', '[7000]', 9000, 'terminology-agent', '2026-01-18T00:00:00Z');
        -- tbox reconciliation
        INSERT INTO tboxreconciliation (id, knowledgesystemid, slot, propertylabel, propertyiri, candidates, choice, chosenlabel, reason, resolvedby, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (20000, 100, 'domain', 'hasColor', 'http://ontopilot.local/ks/100/onto#hasColor', '{""a"": ""A""}', 'common_super', 'Thing', 'rationale', 'agent', '2026-01-19T00:00:00Z');
        -- validation decision
        INSERT INTO validationdecision (id, knowledgesystemid, propertylabel, propertyiri, xsdtype, action, reason, resolvedby, createdat)
            OVERRIDING SYSTEM VALUE
            VALUES (21000, 100, 'age', 'http://ontopilot.local/ks/100/onto#age', 'decimal', 'relax', 'r', 'agent', '2026-01-20T00:00:00Z');
    ";

    /// <summary>Helper that dumps a table's columns + values to the given StringBuilder.</summary>
    private static async Task DumpRowsAsync(string connStr, string table, System.Text.StringBuilder dump)
    {
        await using var c = new NpgsqlConnection(connStr);
        await c.OpenAsync();
        await using (var set = c.CreateCommand())
        {
            set.CommandText = "SET datestyle = 'ISO'; SET timezone = 'UTC'";
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = c.CreateCommand();
        var cols = new List<string>();
        cmd.CommandText = @"SELECT column_name FROM information_schema.columns WHERE table_schema='public' AND table_name=@t ORDER BY ordinal_position";
        cmd.Parameters.AddWithValue("@t", table);
        var r1 = await cmd.ExecuteReaderAsync();
        await using (r1.ConfigureAwait(false))
        {
            while (await r1.ReadAsync())
                cols.Add(r1.GetString(0));
        }
        await r1.DisposeAsync();
        // Dump each row as a single line of "col::text=value" entries, mirroring
        // what the checksum SQL actually concatenates.
        var textCols = cols.Select(c => $"\"{c}\"::text AS \"{c}\"").ToArray();
        cmd.CommandText = $"SELECT {string.Join(", ", textCols)} FROM \"{table}\" ORDER BY id";
        var r2 = await cmd.ExecuteReaderAsync();
        await using (r2.ConfigureAwait(false))
        {
            while (await r2.ReadAsync())
            {
                for (int i = 0; i < cols.Count; i++)
                {
                    dump.Append($"{cols[i]}::text=");
                    if (r2.IsDBNull(i)) dump.Append("<NULL>");
                    else dump.Append(r2.GetString(i));
                    dump.Append("; ");
                }
                dump.AppendLine();
            }
        }
        await r2.DisposeAsync();
    }

    /// <summary>
    /// The required test. Applies the migration, then asserts: (a) every
    /// table's row count is identical to the pre-migration snapshot, (b) no
    /// foreign key was left dangling (zero orphans), (c) per-row business
    /// checksums are byte-identical. This is the canary Task 4's
    /// preflight gate relies on.
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Sql_migration_preserves_rows_and_all_foreign_keys()
    {
        var csb = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        await SeedPythonSchemaAsync(csb);

        var cmd = new SqlMigrationCommand(NullLogger<SqlMigrationCommand>.Instance);
        var before = await SqlSnapshot.CaptureAsync(csb.ConnectionString, CancellationToken.None);

        await cmd.ApplyAsync(csb.ConnectionString, CancellationToken.None);
        var after = await SqlSnapshot.CaptureAsync(csb.ConnectionString, CancellationToken.None);

        Assert.Equal(before.TableCounts, after.TableCounts);
        Assert.All(after.OrphanCounts, pair => Assert.Equal(0, pair.Value));
        Assert.Equal(before.BusinessChecksums, after.BusinessChecksums);
    }

    /// <summary>
    /// Re-running <see cref="SqlMigrationCommand.ApplyAsync"/> on an already
    /// migrated database must be a no-op: the same row counts, zero new
    /// orphans, identical business checksums. The "every step uses IF NOT
    /// EXISTS / DROP IF EXISTS" rule in the brief is what makes this work.
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Apply_is_idempotent_on_second_run()
    {
        var csb = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        await SeedPythonSchemaAsync(csb);

        var cmd = new SqlMigrationCommand(NullLogger<SqlMigrationCommand>.Instance);
        await cmd.ApplyAsync(csb.ConnectionString, CancellationToken.None);
        var first = await SqlSnapshot.CaptureAsync(csb.ConnectionString, CancellationToken.None);

        await cmd.ApplyAsync(csb.ConnectionString, CancellationToken.None);
        var second = await SqlSnapshot.CaptureAsync(csb.ConnectionString, CancellationToken.None);

        Assert.Equal(first.TableCounts, second.TableCounts);
        Assert.All(second.OrphanCounts, pair => Assert.Equal(0, pair.Value));
        Assert.Equal(first.BusinessChecksums, second.BusinessChecksums);
    }

    /// <summary>
    /// After <see cref="SqlMigrationCommand.RollbackAsync"/> the schema must
    /// be readable by the Python / SQLAlchemy backend: every original
    /// <c>id</c> bigint primary key is present, every business column is
    /// present, and the original rows still exist.
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Rollback_restores_python_readable_schema_and_rows()
    {
        var csb = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        await SeedPythonSchemaAsync(csb);

        var cmd = new SqlMigrationCommand(NullLogger<SqlMigrationCommand>.Instance);
        await cmd.ApplyAsync(csb.ConnectionString, CancellationToken.None);
        await cmd.RollbackAsync(csb.ConnectionString, CancellationToken.None);

        await using var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync();

        // After rollback: every table still has its rows (the migration is
        // forward-only on the original PK; rollback drops only the new
        // columns + constraints).
        var expected = new Dictionary<string, long>
        {
            ["users"] = 2,
            ["authsession"] = 2,
            ["provider"] = 2,
            ["knowledgesystem"] = 2,
            ["ksgrant"] = 1,
            ["knowledgepromptoverride"] = 1,
            ["knowledgeapitoken"] = 1,
            ["mcpusertoken"] = 1,
            ["document"] = 1,
            ["chunk"] = 1,
            ["systemconfig"] = 1,
            ["extractionjob"] = 1,
            ["auditevent"] = 1,
            ["axiomprovenance"] = 1,
            ["aboxprovenance"] = 1,
            ["ontologyrelease"] = 1,
            ["releasedeployment"] = 1,
            ["releasestatementprovenance"] = 1,
            ["exportjob"] = 1,
            ["conflict"] = 1,
            ["entityresolution"] = 1,
            ["termproposal"] = 1,
            ["tboxreconciliation"] = 1,
            ["validationdecision"] = 1,
        };

        foreach (var (table, expectedCount) in expected)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = $"SELECT count(*)::bigint FROM {table}";
            var actual = (long)(await c.ExecuteScalarAsync())!;
            Assert.True(actual >= expectedCount,
                $"table {table}: expected at least {expectedCount} rows after rollback, got {actual}");
        }

        // No guid_id / legacy_id columns should remain.
        await using (var c = conn.CreateCommand())
        {
            c.CommandText = @"
                SELECT count(*)::bigint
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND column_name IN ('guid_id', 'legacy_id')";
            var extraColumns = (long)(await c.ExecuteScalarAsync())!;
            Assert.Equal(0, extraColumns);
        }
    }
}
