using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Application.Foundation;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Extraction;
using OnToPilot.Tests.Persistence;
using Oxigraph;
using Xunit;
using OntoNamedNode = Oxigraph.NamedNode;

namespace OnToPilot.Tests.Releases;

[Collection(nameof(ExtractionTestCollection))]
public sealed class ReleaseServiceTests
{
    // -- create + background capture --

    [Fact]
    public async Task CreateDraft_kicks_off_background_capture_ready()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "rel-create");
        await SeedTurtleAsync(app, ks,
            "@prefix ex: <http://example.com/rel-create#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            "ex:Animal a owl:Class .\n", toABox: false);
        var actor = await AdminActorAsync(app);

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var draft = await svc.CreateDraftAsync(ks.Id, actor, "title", "notes", CancellationToken.None);
        Assert.NotNull(draft);

        // Synchronous capture → the row is ready immediately.
        var db = app.CreateDbContext();
        var row = await db.OntologyReleases.AsNoTracking().FirstAsync(r => r.Id == draft!.Id);
        Assert.Equal("ready", row.Manifest!.RootElement.GetProperty("capture_status").GetString());
        // A draft is captured but not yet published → serving store closed.
        var releases = scope.ServiceProvider.GetRequiredService<ReleaseManager>();
        Assert.False(releases.IsPublished(draft.Id.ToString("N")));
    }

    // -- list --

    [Fact]
    public async Task ListAsync_returns_releases()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "rel-list");
        var actor = await AdminActorAsync(app);
        using var scope0 = app.Services.CreateScope();
        await scope0.ServiceProvider.GetRequiredService<ReleaseService>()
            .CreateDraftAsync(ks.Id, actor, "t", "n", CancellationToken.None);

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var out_ = await svc.ListAsync(ks.Id, actor, CancellationToken.None);
        Assert.NotNull(out_);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(out_));
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
    }

    // -- review --

    [Fact]
    public async Task ReviewAsync_draft_to_reviewed()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "rel-review");
        var actor = await AdminActorAsync(app);
        Guid draftId;
        using (var scope0 = app.Services.CreateScope())
        {
            var d = await scope0.ServiceProvider.GetRequiredService<ReleaseService>()
                .CreateDraftAsync(ks.Id, actor, "t", "n", CancellationToken.None);
            draftId = d!.Id;
        }
        await WaitForCaptureReadyAsync(app, draftId, TimeSpan.FromSeconds(10));

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var reviewed = await svc.ReviewAsync(ks.Id, draftId, actor, null, CancellationToken.None);
        Assert.NotNull(reviewed);
        Assert.Equal("reviewed", reviewed!.Status);
    }

    // -- publish --

    [Fact]
    public async Task PublishAsync_reviewed_to_published()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "rel-publish");
        var actor = await AdminActorAsync(app);
        var draftId = await CreateCapturedDraftAsync(app, ks, actor, "rel-publish");
        using var scope0 = app.Services.CreateScope();
        var svc0 = scope0.ServiceProvider.GetRequiredService<ReleaseService>();
        await svc0.ReviewAsync(ks.Id, draftId, actor, null, CancellationToken.None);

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var published = await svc.PublishAsync(ks.Id, draftId, actor, null, CancellationToken.None);
        Assert.NotNull(published);
        Assert.Equal("published", published!.Status);
        Assert.Equal("v1", published.Version);
        var releases = scope.ServiceProvider.GetRequiredService<ReleaseManager>();
        Assert.True(releases.IsPublished(draftId.ToString("N")));
    }

    [Fact]
    public async Task PublishAsync_per_ks_version_increment()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "rel-ver");
        var actor = await AdminActorAsync(app);

        var v1 = await PublishFreshDraftAsync(app, ks, actor);
        var v2 = await PublishFreshDraftAsync(app, ks, actor);
        Assert.Equal("v1", v1);
        Assert.Equal("v2", v2);
    }

    // -- stop deployment --

    [Fact]
    public async Task StopDeployment_closes_serving_store()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "rel-stop");
        var actor = await AdminActorAsync(app);
        var draftId = await PublishCapturedDraftAsync(app, ks, actor, "rel-stop");

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var stopped = await svc.StopDeploymentAsync(ks.Id, draftId, actor, CancellationToken.None);
        Assert.NotNull(stopped);
        var releases = scope.ServiceProvider.GetRequiredService<ReleaseManager>();
        Assert.False(releases.IsPublished(draftId.ToString("N")));
    }

    // -- delete --

    [Fact]
    public async Task DeleteAsync_status_deleted()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "rel-delete");
        var actor = await AdminActorAsync(app);
        var draftId = await PublishCapturedDraftAsync(app, ks, actor, "rel-delete");

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var deleted = await svc.DeleteAsync(ks.Id, draftId, actor, CancellationToken.None);
        Assert.NotNull(deleted);
        Assert.Equal("deleted", deleted!.Status);
        var releases = scope.ServiceProvider.GetRequiredService<ReleaseManager>();
        Assert.False(releases.IsPublished(draftId.ToString("N")));
    }

    [Fact]
    public async Task DeleteAsync_succeeds_for_stuck_pending_capture_and_flips_manifest()
    {
        // Regression: a draft whose capture_status is stuck at "pending"
        // (because the create-draft request was interrupted) could not be
        // deleted — DeleteAsync threw 409 "capture is still running" even
        // though no background capture was actually running (MVP is
        // synchronous). The fix removes the stale "pending" check and
        // flips capture_status to "deleted" so the UI stops showing
        // "正在生成" after the release is deleted.
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "rel-stuck-pending");
        var actor = await AdminActorAsync(app);

        // Seed a release row with a stuck "pending" manifest (simulating
        // an interrupted create-draft where the manifest was never updated).
        var stuckId = Guid.NewGuid();
        var db = app.CreateDbContext();
        db.OntologyReleases.Add(new OntologyReleaseEntity
        {
            Id = stuckId,
            LegacyId = TestLegacyIds.Next("ontology_releases"),
            KnowledgeSystemId = ks.Id,
            Version = $"draft-{stuckId.ToString("N")[..12]}",
            Status = "draft",
            Title = "stuck",
            Notes = "",
            Manifest = JsonDocument.Parse("""{"capture_status":"pending"}"""),
            CreatedById = Guid.Parse(actor.UserId),
            CreatedByName = actor.DisplayName,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var deleted = await svc.DeleteAsync(ks.Id, stuckId, actor, CancellationToken.None);
        Assert.NotNull(deleted);
        Assert.Equal("deleted", deleted!.Status);

        // Manifest must now show capture_status="deleted" (not "pending")
        // so the UI stops showing "正在生成".
        var row = await app.CreateDbContext().OntologyReleases.AsNoTracking()
            .FirstAsync(r => r.Id == stuckId);
        Assert.Equal("deleted",
            row.Manifest!.RootElement.GetProperty("capture_status").GetString());
    }

    // -- rollback --

    [Fact]
    public async Task RollbackAsync_restores_workspace_graphs()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "rel-rollback");
        const string tboxA =
            "@prefix ex: <http://example.com/rel-rollback#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            "ex:Original a owl:Class .\n";
        await SeedTurtleAsync(app, ks, tboxA, toABox: false);
        var actor = await AdminActorAsync(app);
        var draftId = await CreateCapturedDraftAsync(app, ks, actor, "rel-rollback");

        // Mutate the workspace TBox after the snapshot is captured.
        await SeedTurtleAsync(app, ks,
            "@prefix ex: <http://example.com/rel-rollback#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            "ex:Mutated a owl:Class .\n", toABox: false);

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var res = await svc.RollbackAsync(ks.Id, draftId, actor, CancellationToken.None);
        Assert.NotNull(res);

        var store = scope.ServiceProvider.GetRequiredService<StoreWrapper>();
        var tboxQuads = store.Match(graph: new OntoNamedNode(ks.GraphIri));
        Assert.Contains(tboxQuads, q => q.Subject is OntoNamedNode n && n.Value == "http://example.com/rel-rollback#Original");
        Assert.DoesNotContain(tboxQuads, q => q.Subject is OntoNamedNode n && n.Value == "http://example.com/rel-rollback#Mutated");
    }

    // -- diff --

    [Fact]
    public async Task DiffAsync_semantic_diff()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "rel-diff");
        await SeedTurtleAsync(app, ks,
            "@prefix ex: <http://example.com/rel-diff#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            "ex:A a owl:Class .\n", toABox: false);
        var actor = await AdminActorAsync(app);
        var fromId = await CreateCapturedDraftAsync(app, ks, actor, "rel-diff-from");

        // Mutate: add a second class.
        await SeedTurtleAsync(app, ks,
            "@prefix ex: <http://example.com/rel-diff#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            "ex:B a owl:Class .\n", toABox: false);
        var toId = await CreateCapturedDraftAsync(app, ks, actor, "rel-diff-to");

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var diff = await svc.DiffAsync(ks.Id, fromId, toId, actor, CancellationToken.None);
        Assert.NotNull(diff);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(diff));
        var tbox = doc.RootElement.GetProperty("layers").GetProperty("tbox");
        Assert.True(tbox.GetProperty("added").GetInt32() >= 1);
        Assert.Equal(0, tbox.GetProperty("removed").GetInt32());
    }

    // --- helpers ---

    private static async Task<Actor> AdminActorAsync(AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Username == "external-admin");
        if (admin is null)
        {
            admin = new UserEntity
            {
                LegacyId = TestLegacyIds.Next("users"), Id = Guid.NewGuid(),
                Username = "external-admin", DisplayName = "Admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("dummy", workFactor: 4),
                IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(admin);
            await db.SaveChangesAsync();
        }
        return new Actor(admin.Id.ToString(), "Admin");
    }

    private static async Task<KnowledgeSystemEntity> SeedKsAsync(
        AuthTestWebApplicationFactory app, string tag)
    {
        var db = app.CreateDbContext();
        // Ensure the admin user exists so the KS owner FK resolves (mirrors
        // the ExternalApiServiceTests seed pattern — a random Guid trips
        // SQLite FK enforcement on OwnerId).
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Username == "external-admin");
        if (admin is null)
        {
            admin = new UserEntity
            {
                LegacyId = TestLegacyIds.Next("users"), Id = Guid.NewGuid(),
                Username = "external-admin", DisplayName = "Admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("dummy", workFactor: 4),
                IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(admin);
            await db.SaveChangesAsync();
        }
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("ks"), Id = Guid.NewGuid(),
            Name = $"ks-{tag}", Description = tag, OwnerId = admin.Id,
            PublicId = $"pub-{tag}",
            BaseIri = $"http://example.com/{tag}#",
            GraphIri = $"http://example.com/graph/{tag}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks;
    }

    private static async Task SeedTurtleAsync(
        AuthTestWebApplicationFactory app, KnowledgeSystemEntity ks, string turtle, bool toABox)
    {
        using var scope = app.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<StoreWrapper>();
        var ctx = KsContext.FromEntity(ks);
        store.LoadTurtle(Encoding.UTF8.GetBytes(turtle),
            new OntoNamedNode(toABox ? ctx.ABoxGraph : ctx.TBoxGraph));
        await Task.CompletedTask;
    }

    private static async Task<bool> WaitForCaptureReadyAsync(
        AuthTestWebApplicationFactory app, Guid releaseId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var db = app.CreateDbContext();
            var row = await db.OntologyReleases.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == releaseId);
            if (row?.Manifest is JsonDocument manifest
                && manifest.RootElement.TryGetProperty("capture_status", out var s)
                && s.GetString() == "ready")
            {
                return true;
            }
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<Guid> CreateCapturedDraftAsync(
        AuthTestWebApplicationFactory app, KnowledgeSystemEntity ks, Actor actor, string tag)
    {
        using var scope = app.Services.CreateScope();
        var draft = await scope.ServiceProvider.GetRequiredService<ReleaseService>()
            .CreateDraftAsync(ks.Id, actor, tag, "", CancellationToken.None);
        await WaitForCaptureReadyAsync(app, draft!.Id, TimeSpan.FromSeconds(10));
        return draft.Id;
    }

    private static async Task<Guid> PublishCapturedDraftAsync(
        AuthTestWebApplicationFactory app, KnowledgeSystemEntity ks, Actor actor, string tag)
    {
        var draftId = await CreateCapturedDraftAsync(app, ks, actor, tag);
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        await svc.ReviewAsync(ks.Id, draftId, actor, null, CancellationToken.None);
        await svc.PublishAsync(ks.Id, draftId, actor, null, CancellationToken.None);
        return draftId;
    }

    private static async Task<string> PublishFreshDraftAsync(
        AuthTestWebApplicationFactory app, KnowledgeSystemEntity ks, Actor actor)
    {
        // Each publish needs a distinct draft + capture; mutate the
        // workspace slightly so the capture is not a no-op, then create +
        // review + publish.
        await SeedTurtleAsync(app, ks,
            "@prefix ex: <http://example.com/rel-ver#> .\n" +
            "@prefix owl: <http://www.w3.org/2002/07/owl#> .\n" +
            $"ex:Class{Guid.NewGuid():N} a owl:Class .\n", toABox: false);
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var draft = await svc.CreateDraftAsync(ks.Id, actor, "v", "", CancellationToken.None);
        await WaitForCaptureReadyAsync(app, draft!.Id, TimeSpan.FromSeconds(10));
        await svc.ReviewAsync(ks.Id, draft.Id, actor, null, CancellationToken.None);
        var published = await svc.PublishAsync(ks.Id, draft.Id, actor, null, CancellationToken.None);
        return published!.Version;
    }
}
