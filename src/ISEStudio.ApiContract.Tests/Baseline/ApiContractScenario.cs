using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using Oxigraph;

namespace ISEStudio.ApiContract.Tests.Baseline;

/// <summary>
/// Lightweight HTTP harness used by the internal contract theory test.
/// Wraps <see cref="ApiContractWebApplicationFactory"/> with the bare
/// minimum scaffolding to issue one authenticated request against one
/// internal operation. The factory's test authentication handler
/// (<see cref="ApiContractWebApplicationFactory.TestSchemeName"/>) makes
/// every request carry an admin identity, so this harness does not need
/// to log in via the production session handler.
/// </summary>
internal static class ApiContractScenario
{
    /// <summary>
    /// Public-id the harness seeds the demonstration knowledge system
    /// under. Matches the <c>{public_id}</c> / <c>{publicId}</c>
    /// placeholder substitution below so the contract theory data
    /// reaches any route that resolves a KS by public id rather than
    /// by Guid.
    /// </summary>
    private const string DemoPublicId = "demo";

    /// <summary>
    /// Id the demo user is persisted under. Matches
    /// <see cref="ApiContractWebApplicationFactory.ContractTestAuthHandler"/>
    /// which always sets the actor's user id to this constant so the
    /// auth-gated services (mcp_tokens.create, releases.create, …) can
    /// find the corresponding row instead of 500'ing on
    /// "MCP token mint requires an authenticated user".
    /// </summary>
    internal static readonly Guid DemoUserId = new("c0000001-0000-0000-0000-000000000001");

    // Entity IRIs the graph seed writes into the demo KS's TBox / ABox /
    // vocabulary graphs so the mutation arms that require pre-existing
    // graph content (abox.create_individual needs a declared class,
    // abox.add_assertion needs a declared property + individuals,
    // vocabulary.create_concept needs a declared scheme) find their
    // targets instead of 500'ing against an empty Oxigraph store.
    // Shared with BodyForPath so the request body's iri / class_iri /
    // scheme_iri point at exactly the quads the seed wrote.
    private const string DemoClassIri = "http://test/demo#DemoClass";
    private const string DemoPropIri = "http://test/demo#hasFriend";
    private const string AliceIri = "http://test/demo#Alice";
    private const string BobIri = "http://test/demo#Bob";
    private const string DemoSchemeIri = "http://test/demo/vocabulary#scheme";

    /// <summary>
    /// Issue the operation and return the resulting <see cref="HttpResponseMessage"/>.
    /// The factory is created per-call so test cases cannot leak
    /// processes or seeded state into each other; the seed step below
    /// runs against the per-call SQLite file so each case starts from
    /// the same deterministic baseline.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        OperationCase operation,
        CancellationToken cancellationToken = default)
    {
        using var factory = new ApiContractWebApplicationFactory();

        // Seed the demo KS + admin user BEFORE issuing the request so
        // the dispatcher arms that eagerly load a KS by id (e.g.
        // `ks.get`, `ks.update`, `abox.classes`) find the row instead
        // of falling off the cliff with a 500. Without this seed the
        // every `{id} → Guid.NewGuid()` substitution below produces a
        // KS id that doesn't exist, the service throws an unhandled
        // exception, and FastApiErrorMiddleware surfaces it as a 500.
        // The demo user covers the auth/admin routes that resolve
        // `{uid}` against the users table (PATCH/DELETE /api/auth/users)
        // AND the services that look up the actor's user id (mcp_tokens
        // / tokens / releases — those hit "MCP token mint requires an
        // authenticated user" when actor.UserId doesn't resolve to a
        // real UserEntity.Id).
        var seed = await SeedDemoEntitiesAsync(factory, cancellationToken)
            .ConfigureAwait(false);

        // Seed minimal TBox/ABox/vocabulary graph content (one declared
        // class + object property, two individuals, one SKOS scheme) so
        // the mutation arms that validate against existing graph state
        // (abox.create_individual / abox.add_assertion /
        // vocabulary.create_concept) succeed instead of 500'ing. Read
        // arms only assert status + schema, so the extra triples don't
        // disturb the already-passing empty-store cases.
        await SeedDemoGraphAsync(factory, seed.KsId, cancellationToken)
            .ConfigureAwait(false);

        var client = factory.CreateClient();
        using var request = BuildRequest(operation, seed);
        // Attach the test scheme's bearer token so the auth pipeline picks it
        // up. The scheme accepts any value, so a constant placeholder works.
        request.Headers.Add("Authorization", $"ContractTest {Guid.NewGuid():N}");
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage BuildRequest(
        OperationCase operation, Seeded seed)
    {
        // Internal paths embed placeholders like {id}; the test
        // substitutes a fixed id so the controller matches the route and
        // executes the dispatcher. After the Guid migration every
        // {ks_id} slot in the baseline was renamed to {id} and now
        // binds the `:guid` route constraint, so the substitution must
        // also be a Guid — the previous `"1"` placeholder now hits the
        // 404 path because `{id:guid}` rejects a non-Guid value.
        //
        // {id} resolves against the per-call seed so the dispatcher
        // actually finds the KS; {uid} resolves against the seeded
        // *victim* user (not the actor admin) so the auth-admin
        // DELETE/PATCH tests act on a deletable non-admin instead of
        // tripping the "You can't delete yourself" guard; {pid}
        // resolves against the seeded provider so PATCH isn't a 500
        // "not found". The remaining sub-entity placeholders stay random
        // because the dispatcher arms that resolve them (conflicts,
        // documents, jobs, …) already return the empty-array /
        // empty-object placeholder when the referenced row is missing —
        // they're the ones that were already passing in the pre-seed
        // harness.
        var path = operation.Path
            .Replace("{id}", seed.KsId.ToString("N"))
            .Replace("{uid}", seed.VictimId.ToString("N"))
            .Replace("{public_id}", DemoPublicId)
            .Replace("{publicId}", DemoPublicId)
            .Replace("{document_id}", Guid.NewGuid().ToString("N"))
            .Replace("{cid}", Guid.NewGuid().ToString("N"))
            .Replace("{did}", Guid.NewGuid().ToString("N"))
            .Replace("{event_id}", seed.EventId.ToString("N"))
            .Replace("{job_id}", Guid.NewGuid().ToString("N"))
            .Replace("{release_id}", Guid.NewGuid().ToString("N"))
            .Replace("{res_id}", seed.EntityResolutionId.ToString("N"))
            .Replace("{rid}", Guid.NewGuid().ToString("N"))
            .Replace("{token_id}", Guid.NewGuid().ToString("N"))
            .Replace("{pid}", seed.ProviderId.ToString("N"))
            .Replace("{prompt_key}", "extraction.system")
            .Replace("{proposal_id}", Guid.NewGuid().ToString("N"))
            .Replace("{user_id}", Guid.NewGuid().ToString("N"))
            .Replace("{filename}", "export.nq")
            .Replace("{version}", "v1");

        var method = new HttpMethod(operation.Method);
        var request = new HttpRequestMessage(method, path);

        if (method == HttpMethod.Post
            || method == HttpMethod.Put
            || method == HttpMethod.Patch
            || method.Method == "DELETE")
        {
            // Multipart endpoints (documents/upload, rdf/import) ship a
            // form with a file field — the JSON envelope the dispatcher
            // carries can't represent IFormFile, so these two routes
            // bypass the facade and bind [FromForm] directly.
            var multipart = MultipartForPath(operation.Path);
            if (multipart is not null)
            {
                request.Content = multipart;
            }
            else
            {
                // Many POST/PATCH/DELETE endpoints accept an optional body;
                // we ship a path-shaped JSON object so the dispatcher arms
                // that validate required fields (auth.create_user needs
                // username + password, conflicts.resolve needs
                // resolution_id, mcp_tokens.create needs name, …) don't
                // crash on a NRE or InvalidOperationException → 500. The
                // dispatcher arms that don't care about the body still
                // accept the extra fields harmlessly.
                request.Content = new StringContent(
                    BodyForPath(operation.Method, operation.Path),
                    Encoding.UTF8,
                    "application/json");
            }
        }

        return request;
    }

    /// <summary>
    /// Build a multipart/form-data body for the two file-upload paths.
    /// Returns null for every other path so the caller falls back to the
    /// JSON <see cref="BodyForPath"/>. The file names use extensions in
    /// <see cref="Documents.DocumentService.SupportedUploadExtensions"/>
    /// (txt) and a valid turtle payload for the RDF importer.
    /// </summary>
    private static MultipartFormDataContent? MultipartForPath(string path)
    {
        if (path.EndsWith("/documents/upload", StringComparison.OrdinalIgnoreCase))
        {
            var form = new MultipartFormDataContent();
            var fileBytes = Encoding.UTF8.GetBytes("demo upload content");
            var file = new ByteArrayContent(fileBytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "file",
                FileName = "demo.txt",
            };
            form.Add(file);
            form.Add(new StringContent("/"), "folder");
            return form;
        }
        if (path.EndsWith("/rdf/import", StringComparison.OrdinalIgnoreCase))
        {
            var form = new MultipartFormDataContent();
            // Minimal valid turtle declaring one owl:Class. The importer
            // auto-detects the format, partitions the triple into the
            // TBox graph, writes it, then runs the post-mutation
            // conflict-detect / validate / sync / stats pipeline.
            var turtle =
                "@prefix ex: <http://demo#> .\n" +
                "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
                "ex:ImportedClass a owl:Class .\n";
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes(turtle));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/turtle");
            file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "file",
                FileName = "demo.ttl",
            };
            form.Add(file);
            form.Add(new StringContent("auto"), "target");
            form.Add(new StringContent("merge"), "strategy");
            form.Add(new StringContent("auto"), "format");
            return form;
        }
        return null;
    }

    /// <summary>
    /// Return a JSON object the per-path dispatcher arms can accept as a
    /// happy-path body. The fallback is an empty object; the specific
    /// overrides only kick in for paths whose dispatcher arm blows up
    /// on missing required fields. Multipart endpoints (upload / rdf
    /// import) ship <c>{}</c> here — they need multipart bodies which
    /// the dispatcher can't accept anyway; those tests are expected
    /// to fail with a validation error and we don't try to fix them.
    /// </summary>
    /// <remarks>
    /// The <paramref name="method"/> is only consulted where the same
    /// path serves two verbs with different body contracts — e.g.
    /// <c>/vocabulary/concepts</c> is both POST (create, needs an existing
    /// scheme) and PATCH (update, where an absent <c>iri</c> makes the
    /// dispatcher short-circuit to the empty-concept placeholder → 200).
    /// </remarks>
    private static string BodyForPath(string method, string path)
    {
        if (path.Equals("/api/auth/users", StringComparison.OrdinalIgnoreCase))
        {
            return """{"username":"demo","password":"demo12345strong"}""";
        }
        if (path.StartsWith("/api/providers/test", StringComparison.OrdinalIgnoreCase))
        {
            return """{"name":"demo","base_url":"http://demo","api_key":"demo","model":"demo","kind":"llm"}""";
        }
        if (path.Equals("/api/providers", StringComparison.OrdinalIgnoreCase))
        {
            // ProviderCreateRequest.ConcurrencyLimit is a non-nullable int;
            // omitting it deserialises to 0 and ProviderService.CreateAsync
            // rejects concurrency <= 0 → 400.
            return """{"name":"demo","base_url":"http://demo","api_key":"demo","model":"demo","kind":"llm","concurrency_limit":4}""";
        }
        // PATCH /api/providers/{pid} — empty body = no-op patch against
        // the seeded provider.
        if (method == "PATCH"
            && path.StartsWith("/api/providers/", StringComparison.OrdinalIgnoreCase))
        {
            return "{}";
        }
        if (path.Equals("/api/knowledge", StringComparison.OrdinalIgnoreCase))
        {
            return """{"name":"demo","graph_iri":"http://demo","base_iri":"http://demo#"}""";
        }
        if (path.Contains("/conflicts/{cid}/resolve", StringComparison.OrdinalIgnoreCase))
        {
            return """{"resolution_id":"demo"}""";
        }
        // SPARQL query endpoints (external.query, published.query,
        // published.release.query) take {query, max_rows}. Must be a
        // SELECT/ASK to pass the read-only guard.
        if (method == "POST" && path.Contains("/query", StringComparison.OrdinalIgnoreCase))
        {
            return """{"query":"SELECT * WHERE { ?s ?p ?o } LIMIT 5","max_rows":5}""";
        }
        // resolution.resolve needs action ("match" requires individual_iri;
        // "new" mints one and needs ClassIri on the row).
        if (path.Contains("/resolution/{res_id}/resolve", StringComparison.OrdinalIgnoreCase))
        {
            return """{"action":"match","individual_iri":"http://test/DemoClass/instance-1"}""";
        }
        // resolution.edit_decision_reason takes {reason}.
        if (method == "PATCH"
            && path.Contains("/resolution/decisions/{res_id}", StringComparison.OrdinalIgnoreCase))
        {
            return """{"reason":"demo reason"}""";
        }
        // MCP tokens: scopes default to all known MCP scopes (incl
        // mcp:read) when absent. Pinning a non-MCP scope like
        // "ontology:read" trips "mcp:read is required" → 400, so only
        // send the name.
        if (path.EndsWith("/mcp/tokens", StringComparison.OrdinalIgnoreCase))
        {
            return """{"name":"demo"}""";
        }
        if (path.EndsWith("/tokens", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/knowledge/", StringComparison.OrdinalIgnoreCase))
        {
            return """{"name":"demo","scopes":["ontology:read"]}""";
        }
        if (path.EndsWith("/releases", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/knowledge/", StringComparison.OrdinalIgnoreCase))
        {
            return """{"name":"v1","notes":""}""";
        }
        // /vocabulary/concepts is both POST (create) and PATCH (update).
        // POST needs an existing scheme (unseeded → 422, unfixable per-call);
        // PATCH with NO iri makes the dispatcher short-circuit to the
        // empty-concept placeholder → 200.
        if (path.EndsWith("/vocabulary/concepts", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/knowledge/", StringComparison.OrdinalIgnoreCase))
        {
            // POST: create needs an existing scheme — the seeded
            // DemoSchemeIri — plus a non-empty pref_label. PATCH with
            // no iri short-circuits to the empty-concept placeholder → 200.
            return method == "PATCH"
                ? "{}"
                : $$"""{"scheme_iri":"{{DemoSchemeIri}}","pref_label":"demo"}""";
        }
        // CreateScheme requires a non-empty title (else 422).
        if (method == "POST"
            && path.EndsWith("/vocabulary/schemes", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/knowledge/", StringComparison.OrdinalIgnoreCase))
        {
            return """{"title":"demo","iri":"http://demo/scheme"}""";
        }
        // vocabulary.suggest_terms: the dispatcher reads scheme_iri / model
        // via `body?["scheme_iri"]` — the `?.` only guards a null body, not
        // a missing key, so the Dictionary indexer throws
        // KeyNotFoundException → 404 when either key is absent. Both keys
        // MUST be present; an empty chunk_ids list then makes SuggestAsync
        // short-circuit to an empty proposal set → 200.
        if (path.EndsWith("/vocabulary/suggest", StringComparison.OrdinalIgnoreCase))
        {
            return """{"scheme_iri":"http://demo","model":"demo"}""";
        }
        // abox.reset has a confirm=true guard; confirm=false → 500.
        if (path.EndsWith("/abox/reset", StringComparison.OrdinalIgnoreCase))
        {
            return """{"confirm":true}""";
        }
        // abox.create_individual: Label + the seeded class IRI so
        // LoadClassLabelsAsync finds the class (else "Unknown class" → 500).
        if (path.EndsWith("/abox/individuals", StringComparison.OrdinalIgnoreCase))
        {
            return $$"""{"label":"Demo","class_iri":"{{DemoClassIri}}"}""";
        }
        // abox.add_assertion / abox.remove_assertion: object-kind
        // assertion against the seeded Alice → hasFriend → Bob. Both
        // endpoints require the subject to exist; remove is idempotent so
        // it returns the individual envelope even with no prior assertion.
        if (path.EndsWith("/abox/assertions", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/abox/assertions/delete", StringComparison.OrdinalIgnoreCase))
        {
            return $$"""{"subject":"{{AliceIri}}","prop":"{{DemoPropIri}}","kind":"object","target":"{{BobIri}}"}""";
        }
        // abox.fix_violation: a "relax_range" fix op targeting the seeded
        // hasFriend property. UpdateProperty finds it, relaxes its range
        // to string, records the decision, and returns the report → 200.
        if (path.EndsWith("/abox/validate/fix", StringComparison.OrdinalIgnoreCase))
        {
            return $$"""{"op":{"kind":"relax_range","prop":"{{DemoPropIri}}"},"summary":"demo"}""";
        }
        // ontology.edit needs a real op; "add_class" + label creates a
        // class in the empty TBox → 200 (and is a valid no-op-ish write).
        if (path.EndsWith("/ontology/edit", StringComparison.OrdinalIgnoreCase))
        {
            return """{"op":"add_class","label":"Demo"}""";
        }
        // documents.parse_batch: "Select at least one document or folder"
        // → 500 when both lists are empty. A non-empty doc id that matches
        // no row yields 0 parsed → empty batch (200).
        if (path.EndsWith("/documents/parse-batch", StringComparison.OrdinalIgnoreCase))
        {
            return """{"document_ids":["11111111-1111-1111-1111-111111111111"]}""";
        }
        // prompts.update: PromptService.UpdateAsync refuses empty content
        // with ValidationException → 400. Ship a non-empty body so the
        // dispatcher returns the wire PromptOut envelope → 200.
        if (method == "PUT"
            && path.Contains("/prompts/", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/knowledge/", StringComparison.OrdinalIgnoreCase))
        {
            return """{"content":"demo"}""";
        }
        // members: add the seeded admin as a viewer. The seeded KS has a
        // null OwnerId so the "this user is the owner" guard is skipped.
        if (path.EndsWith("/members", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/knowledge/", StringComparison.OrdinalIgnoreCase))
        {
            return """{"username":"contract-admin","role":"viewer"}""";
        }
        return "{}";
    }

    private readonly record struct Seeded(
        Guid KsId, Guid UserId, Guid VictimId, Guid ProviderId, Guid EventId,
        Guid EntityResolutionId);

    /// <summary>
    /// Insert a fresh <see cref="KnowledgeSystemEntity"/> + matching
    /// <see cref="UserEntity"/> into the factory's SQLite database and
    /// return the Guids the test should substitute into the
    /// <c>{id}</c> / <c>{uid}</c> route slots. Run BEFORE issuing the
    /// request so the dispatcher's <c>SingleAsync</c> lookups actually
    /// find a row.
    /// </summary>
    private static async Task<Seeded> SeedDemoEntitiesAsync(
        ApiContractWebApplicationFactory factory,
        CancellationToken cancellationToken)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<ISEStudioDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        var ksId = Guid.NewGuid();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = ksId,
            PublicId = DemoPublicId,
            Name = "Demo Knowledge System",
            Description = string.Empty,
            GraphIri = $"http://test/{DemoPublicId}",
            BaseIri = $"http://test/{DemoPublicId}#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Demo user is persisted under DemoUserId (Guid.Empty) so it
        // matches the actor the test auth handler stashes on the
        // HttpContext. Without this alignment mcp_tokens.create /
        // tokens.create / releases.create 500 with "MCP token mint
        // requires an authenticated user" because the service looks
        // up actor.UserId against the users table and finds nothing.
        db.Users.Add(new UserEntity
        {
            Id = DemoUserId,
            Username = "contract-admin",
            DisplayName = "Contract Admin",
            PasswordHash = string.Empty,
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Second user the auth-admin DELETE/PATCH tests act on. The actor
        // is always the admin (DemoUserId), so deleting THIS user never trips
        // the "You can't delete yourself" guard; it's a non-admin that owns
        // no knowledge system, so it clears the "owns N KS" guard too.
        var victimId = Guid.NewGuid();
        db.Users.Add(new UserEntity
        {
            Id = victimId,
            Username = "contract-victim",
            DisplayName = "Contract Victim",
            PasswordHash = string.Empty,
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // A provider row the PATCH /api/providers/{pid} test resolves. A
        // random {pid} hits "Provider {id} not found" → 500; binding the
        // seeded provider's id makes the empty-body PATCH a no-op → 200.
        var providerId = Guid.NewGuid();
        db.Providers.Add(new ProviderEntity
        {
            Id = providerId,
            Name = "Contract Provider",
            BaseUrl = "http://demo",
            ApiKey = "demo",
            Model = "demo",
            Kind = "llm",
            ConcurrencyLimit = 4,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // An audit event the history.rollback case resolves. A random
        // {event_id} hits 404 (KeyNotFoundException); binding the seeded
        // event's id makes rollback 200 (non-empty Added blob skips the
        // no-diff 400 guard). Graph=null → TBox (ks.GraphIri at replay).
        var eventId = Guid.NewGuid();
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = eventId,
            KnowledgeSystemId = ksId,
            ActorId = DemoUserId,
            ActorName = "Contract Admin",
            Action = "ontology.edit",
            Summary = "contract seed edit",
            Graph = null,
            Added = System.Text.Encoding.UTF8.GetBytes(
                $"<urn:Seed> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://www.w3.org/2002/07/owl#Class> <http://test/{DemoPublicId}> .\n"),
            Removed = null,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // A pending entity-resolution row the resolution.* cases resolve.
        // A random {res_id} hits ResolveResRowGuidAsync's null-return
        // branch and degrades to the empty placeholder → schema still
        // green but no real shape verification; binding the seeded row's
        // Guid id makes resolve / revoke / edit_reason land on a real
        // row. resolve requires ClassIri so the 'new' action can mint;
        // match needs none.
        var entityResolutionId = Guid.NewGuid();
        db.EntityResolutions.Add(new EntityResolutionEntity
        {
            Id = entityResolutionId,
            KnowledgeSystemId = ksId,
            SurfaceForm = "demo",
            ClassIri = "http://test/DemoClass",
            Status = "pending",
            Confidence = 0.5,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Round-trip check — without this the next test failure
        // surfaces as a misleading 404/500 downstream instead of
        // telling us the seed itself didn't persist. A handful of
        // schema quirks (Guid.Empty user id rejected by SQLite
        // identity, FK target row missing) only show up here.
        db.ChangeTracker.Clear();
        var seededKs = await db.KnowledgeSystems.AnyAsync(k => k.Id == ksId, cancellationToken)
            .ConfigureAwait(false);
        var seededUser = await db.Users.AnyAsync(u => u.Id == DemoUserId, cancellationToken)
            .ConfigureAwait(false);
        if (!seededKs || !seededUser)
        {
            throw new InvalidOperationException(
                $"Contract harness seed failed (ks={seededKs}, user={seededUser}).");
        }

        return new Seeded(ksId, DemoUserId, victimId, providerId, eventId, entityResolutionId);
    }

    /// <summary>
    /// Write the minimum graph content the mutation arms validate
    /// against: one declared <c>owl:Class</c> + one
    /// <c>owl:ObjectProperty</c> in the TBox, two
    /// <c>owl:NamedIndividual</c> instances in the ABox, and one
    /// <c>skos:ConceptScheme</c> in the vocabulary graph. The IRIs match
    /// the <c>DemoClassIri</c> / <c>DemoPropIri</c> / <c>AliceIri</c> /
    /// <c>BobIri</c> / <c>DemoSchemeIri</c> constants BodyForPath emits
    /// so the request body lands on the quads this seed wrote.
    ///
    /// <para>Read arms only assert HTTP status + JSON schema, so the
    /// extra triples don't disturb the empty-store read cases — they
    /// merely return non-empty arrays/objects instead of empty ones.
    /// A failure to resolve the StoreWrapper (hand-built factory) is a
    /// no-op so the harness degrades to the empty-store behaviour.</para>
    /// </summary>
    private static async Task SeedDemoGraphAsync(
        ApiContractWebApplicationFactory factory,
        Guid ksId,
        CancellationToken cancellationToken)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetService<StoreWrapper>();
        var db = scope.ServiceProvider
            .GetRequiredService<ISEStudioDbContext>();
        if (store is null) return;
        var ks = await db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, cancellationToken)
            .ConfigureAwait(false);
        if (ks is null) return;

        var ksc = KsContext.FromEntity(ks);
        var tbox = new NamedNode(ksc.TBoxGraph);
        var abox = new NamedNode(ksc.ABoxGraph);
        var vocab = new NamedNode(ksc.VocabularyGraph);

        var cls = new NamedNode(DemoClassIri);
        var prop = new NamedNode(DemoPropIri);
        var alice = new NamedNode(AliceIri);
        var bob = new NamedNode(BobIri);
        var scheme = new NamedNode(DemoSchemeIri);
        var now = DateTimeOffset.UtcNow.ToString("o");
        var zh = "zh-CN";

        store.AddQuads(tbox, new Quad[]
        {
            new(cls, Vocabulary.RdfType, Vocabulary.OwlClass, tbox),
            new(cls, Vocabulary.RdfsLabel, new Literal("Demo Class", Language: zh), tbox),
            new(prop, Vocabulary.RdfType, Vocabulary.OwlObjectProperty, tbox),
            new(prop, Vocabulary.RdfsLabel, new Literal("has friend", Language: zh), tbox),
        });
        store.AddQuads(abox, new Quad[]
        {
            new(alice, Vocabulary.RdfType, Vocabulary.OwlNamedIndividual, abox),
            new(alice, Vocabulary.RdfType, cls, abox),
            new(alice, Vocabulary.RdfsLabel, new Literal("Alice", Language: zh), abox),
            new(bob, Vocabulary.RdfType, Vocabulary.OwlNamedIndividual, abox),
            new(bob, Vocabulary.RdfType, cls, abox),
            new(bob, Vocabulary.RdfsLabel, new Literal("Bob", Language: zh), abox),
        });
        store.AddQuads(vocab, new Quad[]
        {
            new(scheme, Vocabulary.RdfType, SkosVocab.ConceptScheme, vocab),
            new(scheme, SkosVocab.DcTitle, new Literal("Demo Scheme", Language: zh), vocab),
            new(scheme, SkosVocab.OpDefaultLanguage, new Literal(zh), vocab),
            new(scheme, SkosVocab.OpOrigin, new Literal("manual"), vocab),
            new(scheme, SkosVocab.DcCreated, new Literal(now), vocab),
            new(scheme, SkosVocab.DcModified, new Literal(now), vocab),
        });
    }

}
