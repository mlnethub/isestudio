using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Authorization;

/// <summary>
/// Drives every row of <c>Authorization/rbac_matrix_expected.json</c>
/// through the live HTTP auth pipeline of a real
/// <see cref="AuthTestWebApplicationFactory"/> host.
///
/// <para>Each matrix row is one <c>[Theory]</c> case. For each of the five
/// actors (anonymous / viewer / editor / owner / admin) the test seeds a
/// fresh knowledge system with viewer+editor grants plus one document,
/// authenticates via a real <c>authsession</c> row + session cookie
/// (same pipeline the login endpoint produces; only the password check is
/// bypassed), substitutes the route tokens, and asserts the exact HTTP
/// status pinned in the JSON. Any mismatch is a CI failure — the JSON is
/// the current-state contract, kept in lock-step with runtime behavior.</para>
///
/// <para>Token substitution uses the real controller template tokens:
/// <c>{id:guid}</c>, <c>{user_id}</c>, <c>{document_id:guid}</c>,
/// <c>{cid}</c>, <c>{rid}</c>, <c>{did}</c>, <c>{job_id}</c>,
/// <c>{event_id}</c>, <c>{res_id}</c>, <c>{release_id}</c>,
/// <c>{proposal_id}</c>, <c>{token_id}</c>, <c>{prompt_key}</c>,
/// <c>{filename}</c>. Entities that a route resolves by FK (KS, user,
/// document) are seeded per actor; other ids are fresh GUIDs whose
/// not-found responses are part of the recorded contract.</para>
///
/// <para>Re-calibration helper: set env var <c>RBAC_MATRIX_DUMP</c> to an
/// output path and the test records the actual status of every
/// (endpoint, actor) pair to that file instead of asserting — the
/// resulting map can be diffed against the expected JSON.</para>
/// </summary>
public sealed class EndpointRoleMatrixTests
{
    // One shared host for all theory cases: rebuilding the factory per
    // case (the repo's per-test idiom) would construct the ASP.NET host
    // 103 times. The shared host keeps one SQLite database for the whole
    // class; every case seeds its own per-actor knowledge systems so
    // mutations (KS delete, member removal, doc delete) never leak across
    // cases or actors. Users and sessions are shared and seeded once.
    private static readonly Lazy<AuthTestWebApplicationFactory> s_factory =
        new(() => new AuthTestWebApplicationFactory());

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> s_matrix =
        LoadMatrix();

    private static readonly string? DumpPath =
        Environment.GetEnvironmentVariable("RBAC_MATRIX_DUMP");

    private static int s_recordModeBannerShown;

    // ---- shared seed state (guarded: xUnit runs a class's tests serially,
    // but the guard also keeps the dump file consistent) -------------------

    private static readonly SemaphoreSlim s_seedLock = new(1, 1);
    private static bool s_seeded;
    private static Guid s_viewerId;
    private static Guid s_editorId;
    private static Guid s_ownerId;
    private static readonly Dictionary<string, string> s_cookieHeaders =
        new(StringComparer.Ordinal); // actor key -> "isestudio_session=<token>"

    private static readonly object s_dumpLock = new();
    private static readonly Dictionary<string, Dictionary<string, int>> s_dumpActuals =
        new(StringComparer.Ordinal);

    public static IEnumerable<object[]> Entries()
    {
        foreach (var (key, actors) in s_matrix)
        {
            var (verb, path) = SplitVerbPath(key);
            yield return new object[] { key, verb, path, actors };
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public async Task Each_endpoint_respects_role_matrix(
        string key,
        string verb,
        string pathTemplate,
        IReadOnlyDictionary<string, int> actors)
    {
        await EnsureSeededAsync();

        foreach (var (actor, expected) in ActorExpectations(actors))
        {
            await AssertActorAsync(key, verb, pathTemplate, actor, expected);
        }
    }

    // ---- actors -----------------------------------------------------------

    private static IEnumerable<(string Actor, int Expected)> ActorExpectations(
        IReadOnlyDictionary<string, int> actors)
    {
        yield return ("anonymous", actors["anonymous"]);
        yield return ("viewer", actors["viewer"]);
        yield return ("editor", actors["editor"]);
        yield return ("owner", actors["owner"]);
        yield return ("admin", actors["admin"]);
    }

    private static async Task AssertActorAsync(
        string key,
        string verb,
        string pathTemplate,
        string actor,
        int expected)
    {
        var (ksId, docId) = await SeedActorWorldAsync();
        var concretePath = Substitute(pathTemplate, ksId, docId);

        var client = s_factory.Value.CreateClient();
        try
        {
            if (s_cookieHeaders.TryGetValue(actor, out var cookie))
            {
                client.DefaultRequestHeaders.Add("Cookie", cookie);
            }

            HttpResponseMessage resp = verb switch
            {
                "GET" => await client.GetAsync(concretePath),
                // rdf/import carries [Consumes("multipart/form-data")]: a
                // JSON body would be rejected at action selection (415)
                // before [Authorize] ever runs — send a multipart body so
                // the row exercises the real auth pipeline.
                "POST" when concretePath.Contains("/rdf/import", StringComparison.Ordinal) =>
                    await client.PostAsync(concretePath, new MultipartFormDataContent()),
                "POST" => await client.PostAsJsonAsync(concretePath, new { }),
                "PUT" => await client.PutAsJsonAsync(concretePath, new { }),
                "PATCH" => await client.PatchAsJsonAsync(concretePath, new { }),
                // Some DELETE actions bind a [FromBody] payload (e.g.
                // vocabulary/concepts + schemes); a body-less DELETE is
                // rejected with 415 before authorization runs. Always send
                // an empty JSON object so those rows hit the auth pipeline.
                "DELETE" => await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, concretePath)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                }),
                _ => throw new InvalidOperationException($"Unsupported verb {verb}"),
            };

            var actual = (int)resp.StatusCode;
            if (DumpPath is not null)
            {
                // Record mode must never silently disable the 515 assertions:
                // print a once-per-run banner so CI logs show the no-op.
                if (Interlocked.Exchange(ref s_recordModeBannerShown, 1) == 0)
                {
                    Console.WriteLine("!! RBAC_MATRIX_DUMP is set — RECORD MODE: matrix assertions are DISABLED, writing actuals to: " + DumpPath);
                }
                RecordActual(key, actor, actual);
                return;
            }

            if (actual != expected)
            {
                var body = await resp.Content.ReadAsStringAsync();
                Assert.Fail(
                    $"matrix row mismatch: {verb} {concretePath}\n" +
                    $"  actor={actor} expected={expected} actual={actual}\n" +
                    $"  body={Truncate(body, 240)}");
            }
        }
        finally
        {
            client.Dispose();
        }
    }

    // ---- seeding ----------------------------------------------------------

    /// <summary>
    /// Seeds the four shared users (admin + mx-viewer / mx-editor /
    /// mx-owner) and mints one <c>authsession</c> row per actor. Sessions
    /// are minted directly (the same rows the login endpoint creates)
    /// instead of replaying 515 BCrypt login round-trips; the handler,
    /// <c>[Authorize]</c> and <c>[KSRoleAuthorize]</c> all run for real.
    /// </summary>
    private static async Task EnsureSeededAsync()
    {
        if (s_seeded) return;
        await s_seedLock.WaitAsync();
        try
        {
            if (s_seeded) return;

            var app = s_factory.Value;
            await app.SeedAdminAsync();
            await app.SeedUserAsync("mx-viewer");
            await app.SeedUserAsync("mx-editor");
            await app.SeedUserAsync("mx-owner");

            var db = app.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            foreach (var (username, actor) in new[]
                     {
                         (AuthTestWebApplicationFactory.AdminUsername, "admin"),
                         ("mx-viewer", "viewer"),
                         ("mx-editor", "editor"),
                         ("mx-owner", "owner"),
                     })
            {
                var user = db.Users.Single(u => u.Username == username);
                var token = $"mx-session-{actor}";
                db.AuthSessions.Add(new AuthSessionEntity
                {
                    LegacyId = TestLegacyIds.Next("authsession"),
                    Token = token,
                    UserId = user.Id,
                    CreatedAt = now,
                    ExpiresAt = now.AddHours(1),
                });
                s_cookieHeaders[actor] = $"isestudio_session={token}";
                if (actor == "viewer") s_viewerId = user.Id;
                if (actor == "editor") s_editorId = user.Id;
                if (actor == "owner") s_ownerId = user.Id;
            }
            db.SaveChanges();

            s_seeded = true;
        }
        finally
        {
            s_seedLock.Release();
        }
    }

    /// <summary>
    /// Seeds a pristine per-actor world: one knowledge system owned by
    /// <c>mx-owner</c> with viewer/editor grants, plus one document row so
    /// the document-scoped routes resolve a real FK. Returns the ids used
    /// for token substitution.
    /// </summary>
    private static async Task<(Guid KsId, Guid DocId)> SeedActorWorldAsync()
    {
        var app = s_factory.Value;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();

        var now = DateTimeOffset.UtcNow;
        var stamp = Guid.NewGuid().ToString("N");
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = stamp,
            Name = $"matrix-{stamp[..8]}",
            Description = "RBAC matrix probe",
            OwnerId = s_ownerId,
            GraphIri = $"http://goodcrew.local/ks/{stamp}",
            BaseIri = $"http://goodcrew.local/ks/{stamp}#",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.KnowledgeSystems.Add(ks);
        db.KSGrants.Add(new KSGrantEntity
        {
            LegacyId = TestLegacyIds.Next("ksgrant"),
            KnowledgeSystemId = ks.Id,
            UserId = s_viewerId,
            Role = "viewer",
            CreatedAt = now,
        });
        db.KSGrants.Add(new KSGrantEntity
        {
            LegacyId = TestLegacyIds.Next("ksgrant"),
            KnowledgeSystemId = ks.Id,
            UserId = s_editorId,
            Role = "editor",
            CreatedAt = now,
        });
        var doc = new DocumentEntity
        {
            LegacyId = TestLegacyIds.Next("document"),
            KnowledgeSystemId = ks.Id,
            Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            OriginalFilename = "matrix-probe.txt",
            Folder = "/",
            Ext = "txt",
            Mime = "text/plain",
            SizeBytes = 0,
            StoragePath = "00/probe",
            UploadedAt = now,
            ParseStatus = "pending",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return (ks.Id, doc.Id);
    }

    // ---- token substitution -----------------------------------------------

    private static string Substitute(string pathTemplate, Guid ksId, Guid docId)
    {
        return pathTemplate
            .Replace("{id:guid}", ksId.ToString())
            .Replace("{user_id}", s_viewerId.ToString())
            .Replace("{document_id:guid}", docId.ToString())
            .Replace("{cid}", Guid.NewGuid().ToString())
            .Replace("{rid}", Guid.NewGuid().ToString())
            .Replace("{did}", Guid.NewGuid().ToString())
            .Replace("{job_id}", Guid.NewGuid().ToString())
            .Replace("{event_id}", Guid.NewGuid().ToString())
            .Replace("{res_id}", Guid.NewGuid().ToString())
            .Replace("{release_id}", Guid.NewGuid().ToString())
            .Replace("{proposal_id}", Guid.NewGuid().ToString())
            .Replace("{token_id}", Guid.NewGuid().ToString())
            .Replace("{prompt_key}", "matrix-probe")
            .Replace("{filename}", "probe.ttl");
    }

    // ---- matrix loading / dump --------------------------------------------

    private static void RecordActual(string key, string actor, int status)
    {
        lock (s_dumpLock)
        {
            if (!s_dumpActuals.TryGetValue(key, out var actors))
            {
                actors = new Dictionary<string, int>(StringComparer.Ordinal);
                s_dumpActuals[key] = actors;
            }
            actors[actor] = status;
            File.WriteAllText(
                DumpPath!,
                JsonSerializer.Serialize(s_dumpActuals,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static (string Verb, string Path) SplitVerbPath(string key)
    {
        var idx = key.IndexOf(' ');
        return (key.Substring(0, idx), key.Substring(idx + 1));
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> LoadMatrix()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("rbac_matrix_expected.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        var doc = JsonDocument.Parse(stream);
        var result = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        var canonicalActors = new[] { "anonymous", "viewer", "editor", "owner", "admin" };
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith('_')) continue; // _meta
            var actors = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var actor in prop.Value.EnumerateObject())
            {
                // The JSON is the contract authority: a misspelled actor key
                // would otherwise be silently ignored forever (ActorExpectations
                // only reads the 5 canonical keys). Fail discovery loud instead.
                if (!canonicalActors.Contains(actor.Name, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Unknown actor '{actor.Name}' in matrix row '{prop.Name}'. Canonical actors: anonymous/viewer/editor/owner/admin");
                }
                actors[actor.Name] = actor.Value.GetInt32();
            }
            result[prop.Name] = actors;
        }
        return result;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
