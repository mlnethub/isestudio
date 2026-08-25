using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Api;
using ISEStudio.Application.Foundation;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Prompts;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Xunit;

namespace ISEStudio.Tests.Prompts;

[Collection(nameof(ExtractionTestCollection))]
public sealed class PromptServiceTests
{
    [Fact]
    public async Task ListAsync_returns_catalog_with_no_overrides_when_db_empty()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "prompts-list-empty");

        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PromptService>();

        var res = await svc.ListAsync(ks.Id, actor, CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(0, res!.TotalOverrides);
        Assert.Equal(PromptCatalog.All.Count, res.Items.Count);
        Assert.All(res.Items, i =>
        {
            Assert.False(i.IsOverridden);
            Assert.Equal(i.DefaultContent, i.EffectiveContent);
            Assert.Null(i.UpdatedAt);
            Assert.Null(i.UpdatedBy);
        });
    }

    [Fact]
    public async Task ListAsync_merges_catalog_with_persisted_override()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "prompts-list-merge");

        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PromptService>();
        await svc.UpdateAsync(ks.Id, "extraction.system", "OVERRIDDEN", actor, CancellationToken.None);

        var res = await svc.ListAsync(ks.Id, actor, CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(1, res!.TotalOverrides);
        var ext = res.Items.Single(i => i.Key == "extraction.system");
        Assert.True(ext.IsOverridden);
        Assert.Equal("OVERRIDDEN", ext.EffectiveContent);
        Assert.NotNull(ext.UpdatedAt);
        Assert.Equal(admin.DisplayName, ext.UpdatedBy);
        // categories of non-overridden items remain defaults
        var review = res.Items.Single(i => i.Key == "review.system");
        Assert.False(review.IsOverridden);
        Assert.Equal(review.DefaultContent, review.EffectiveContent);
    }

    [Fact]
    public async Task UpdateAsync_inserts_then_updates_and_audits()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "prompts-update-upsert");
        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PromptService>();

        var first = await svc.UpdateAsync(ks.Id, "extraction.system", "FIRST", actor, CancellationToken.None);
        Assert.NotNull(first);
        Assert.True(first!.IsOverridden);
        Assert.Equal("FIRST", first.EffectiveContent);
        Assert.Equal(admin.DisplayName, first.UpdatedBy);
        Assert.NotNull(first.UpdatedAt);

        var rowAfterInsert = db.KnowledgePromptOverrides.AsNoTracking()
            .Single(o => o.KnowledgeSystemId == ks.Id && o.PromptKey == "extraction.system");
        Assert.Equal("FIRST", rowAfterInsert.Content);
        var firstLegacyId = rowAfterInsert.LegacyId;
        Assert.True(firstLegacyId > 0);

        var second = await svc.UpdateAsync(ks.Id, "extraction.system", "SECOND", actor, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal("SECOND", second!.EffectiveContent);

        var rowAfterUpdate = db.KnowledgePromptOverrides.AsNoTracking()
            .Single(o => o.KnowledgeSystemId == ks.Id && o.PromptKey == "extraction.system");
        Assert.Equal("SECOND", rowAfterUpdate.Content);
        Assert.Equal(firstLegacyId, rowAfterUpdate.LegacyId); // upsert, not insert

        var audits = db.AuditEvents.AsNoTracking()
            .Where(e => e.KnowledgeSystemId == ks.Id && e.Action == "system.prompt.override").ToList();
        Assert.Equal(2, audits.Count);
    }

    [Fact]
    public async Task UpdateAsync_throws_400_on_empty_content_and_unknown_key()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "prompts-validation");
        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PromptService>();

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.UpdateAsync(ks.Id, "extraction.system", "   ", actor, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.UpdateAsync(ks.Id, "no.such.key", "x", actor, CancellationToken.None));
    }

    [Fact]
    public async Task RestoreAsync_removes_override_and_audits()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "prompts-restore");
        db.KnowledgePromptOverrides.Add(new KnowledgePromptOverrideEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ks.Id,
            PromptKey = "extraction.system",
            Content = "X",
            UpdatedById = admin.Id,
            UpdatedByName = admin.DisplayName ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PromptService>();

        var res = await svc.RestoreAsync(ks.Id, "extraction.system", actor, CancellationToken.None);

        Assert.NotNull(res);
        Assert.False(res!.IsOverridden);
        Assert.Equal(res.DefaultContent, res.EffectiveContent);
        Assert.Null(res.UpdatedAt);
        Assert.Empty(db.KnowledgePromptOverrides.Where(o => o.KnowledgeSystemId == ks.Id));
        Assert.True(db.AuditEvents.AsNoTracking().Any(e => e.Action == "system.prompt.restore"));
    }

    [Fact]
    public async Task RestoreAsync_noop_when_no_override()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "prompts-restore-noop");
        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PromptService>();

        var res = await svc.RestoreAsync(ks.Id, "review.system", actor, CancellationToken.None);

        Assert.NotNull(res);
        Assert.False(res!.IsOverridden);
        Assert.Empty(db.AuditEvents.Where(e => e.KnowledgeSystemId == ks.Id && e.Action == "system.prompt.restore"));
    }

    [Fact]
    public async Task RestoreAllAsync_removes_every_override_and_audits_aggregate()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "prompts-restore-all");

        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PromptService>();
        await svc.UpdateAsync(ks.Id, "extraction.system", "A", actor, CancellationToken.None);
        await svc.UpdateAsync(ks.Id, "review.system", "B", actor, CancellationToken.None);

        var n = await svc.RestoreAllAsync(ks.Id, actor, CancellationToken.None);

        Assert.Equal(2, n);
        Assert.Empty(db.KnowledgePromptOverrides.Where(o => o.KnowledgeSystemId == ks.Id));
        var agg = db.AuditEvents.AsNoTracking().Single(e => e.Action == "system.prompt.restore_all");
        Assert.NotNull(agg.Detail);
    }

    private static async Task SeedAdminAsync(AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername)) return;
        db.Users.Add(new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"), Id = Guid.NewGuid(),
            Username = AuthTestWebApplicationFactory.AdminUsername,
            DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(AuthTestWebApplicationFactory.AdminPassword, workFactor: 4),
            IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<KnowledgeSystemEntity> CreateKsAsync(ISEStudioDbContext db, string tag)
    {
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("ks"), Id = Guid.NewGuid(),
            Name = $"ks-{tag}", Description = tag,
            OwnerId = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id,
            BaseIri = $"http://example.com/{tag}#",
            GraphIri = $"http://example.com/graph/{tag}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks); await db.SaveChangesAsync();
        return ks;
    }
}