using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Api;
using ISEStudio.Application.Foundation;
using ISEStudio.EntityResolution;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Xunit;

namespace ISEStudio.Tests.EntityResolution;

[Collection(nameof(ExtractionTestCollection))]
public sealed class ResolutionServiceTests
{
    [Fact]
    public async Task ListQueueAsync_filters_by_status_and_returns_paging_envelope()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, admin.Id, "resolution-queue");

        await SeedRowAsync(db, ks.Id, "apple", "pending");
        await SeedRowAsync(db, ks.Id, "banana", "pending");
        await SeedRowAsync(db, ks.Id, "cherry", "matched");

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ResolutionService>();
        var res = await svc.ListQueueAsync(ks.Id, query: null, limit: 50, offset: 0,
            new Actor(admin.Id.ToString()), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(2, res!.Total);
        Assert.Equal(2, res.Items.Count);
        // queue is filtered to status="pending" server-side; surfaces are
        // a strict subset of the seed set (no matched rows leak in).
        var surfaces = res.Items.Select(i => i.SurfaceForm).ToHashSet();
        Assert.Equal(new HashSet<string> { "apple", "banana" }, surfaces);
    }

    [Fact]
    public async Task ListDecisionsAsync_returns_only_resolved_rows_in_reverse_chrono()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, admin.Id, "resolution-decisions");
        await SeedRowAsync(db, ks.Id, "pending-1", "pending");
        await SeedRowAsync(db, ks.Id, "matched-1", "matched");
        await SeedRowAsync(db, ks.Id, "new-1", "new");

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ResolutionService>();
        var res = await svc.ListDecisionsAsync(ks.Id, query: null, limit: 50, offset: 0,
            new Actor(admin.Id.ToString()), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(2, res!.Total);
        Assert.Contains(res.Items, i => i.Status == "matched");
        Assert.Contains(res.Items, i => i.Status == "new");
        Assert.DoesNotContain(res.Items, i => i.Status == "pending");
    }

    [Fact]
    public async Task ResolveAsync_match_sets_individual_iri_and_audits_abox_resolve()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, admin.Id, "resolution-match");
        var row = await SeedRowAsync(db, ks.Id, "fig", "pending");

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ResolutionService>();
        var res = await svc.ResolveAsync(ks.Id, row.Id, action: "match",
            individualIri: "http://example.com/individuals/fig-1",
            new Actor(admin.Id.ToString()), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal("matched", res!.Status);
        Assert.Equal("http://example.com/individuals/fig-1", res.IndividualIri);
        Assert.Equal(admin.DisplayName, res.ResolvedBy);
        Assert.NotNull(res.ResolvedAt);
        Assert.True(db.AuditEvents.AsNoTracking()
            .Any(e => e.KnowledgeSystemId == ks.Id && e.Action == "abox.resolve"));
    }

    [Fact]
    public async Task ResolveAsync_match_without_individual_iri_throws_400()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, admin.Id, "resolution-match-validation");
        var row = await SeedRowAsync(db, ks.Id, "fig", "pending");

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ResolutionService>();
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ResolveAsync(ks.Id, row.Id, action: "match", individualIri: null,
                new Actor(admin.Id.ToString()), CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_invalid_action_throws_400()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, admin.Id, "resolution-action-validation");
        var row = await SeedRowAsync(db, ks.Id, "fig", "pending");

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ResolutionService>();
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ResolveAsync(ks.Id, row.Id, action: "merge", individualIri: null,
                new Actor(admin.Id.ToString()), CancellationToken.None));
    }

    [Fact]
    public async Task RevokeAsync_removes_row_and_audits_resolution_revoke()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, admin.Id, "resolution-revoke");
        var row = await SeedRowAsync(db, ks.Id, "fig", "matched");

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ResolutionService>();
        var ok = await svc.RevokeAsync(ks.Id, row.Id,
            new Actor(admin.Id.ToString()), CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(db.EntityResolutions.Where(r => r.KnowledgeSystemId == ks.Id));
        Assert.True(db.AuditEvents.AsNoTracking()
            .Any(e => e.KnowledgeSystemId == ks.Id && e.Action == "resolution.revoke"));
    }

    [Fact]
    public async Task EditReasonAsync_writes_context_json_and_audits_resolution_edit_reason()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, admin.Id, "resolution-reason");
        var row = await SeedRowAsync(db, ks.Id, "fig", "matched");

        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ResolutionService>();
        var res = await svc.EditReasonAsync(ks.Id, row.Id, reason: "manual override",
            new Actor(admin.Id.ToString()), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal("manual override", res!.Reason);
        var reread = db.EntityResolutions.AsNoTracking()
            .Single(r => r.KnowledgeSystemId == ks.Id && r.Id == row.Id);
        Assert.NotNull(reread.Context);
        using var doc = reread.Context!;
        Assert.Equal("manual override", doc.RootElement.GetProperty("reason").GetString());
        Assert.True(db.AuditEvents.AsNoTracking()
            .Any(e => e.KnowledgeSystemId == ks.Id && e.Action == "resolution.edit_reason"));
    }

    // --- helpers ---

    private static async Task SeedAdminAsync(AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername)) return;
        db.Users.Add(new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = AuthTestWebApplicationFactory.AdminUsername,
            DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(AuthTestWebApplicationFactory.AdminPassword, workFactor: 4),
            IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<KnowledgeSystemEntity> CreateKsAsync(ISEStudioDbContext db, Guid ownerId, string tag)
    {
        var ks = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            Name = $"ks-{tag}", Description = tag, OwnerId = ownerId,
            BaseIri = $"http://example.com/{tag}#", GraphIri = $"http://example.com/graph/{tag}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks); await db.SaveChangesAsync(); return ks;
    }

    private static async Task<EntityResolutionEntity> SeedRowAsync(
        ISEStudioDbContext db, Guid ksId, string surface, string status)
    {
        var row = new EntityResolutionEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId,
            SurfaceForm = surface,
            ClassIri = "http://example.com/Fruit",
            Status = status,
            Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.EntityResolutions.Add(row);
        await db.SaveChangesAsync();
        return row;
    }
}
