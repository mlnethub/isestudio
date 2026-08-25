using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Application.Foundation;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Xunit;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// Service-level tests for <see cref="OntologyService.GetViewAsync"/>:
/// happy path (admin gets the curated view with
/// <see cref="KnowledgeSystemMeta"/> attached), not-found (unknown KS
/// id returns <c>null</c>), and access denial (sub-Viewer caller hits
/// <see cref="InvalidOperationException"/>). Mirrors the established
/// service-test pattern: real Kestrel via
/// <see cref="AuthTestWebApplicationFactory"/>, SQLite + per-test temp
/// roots, admin user seeded in-line.
/// </summary>
[Collection(nameof(ExtractionTestCollection))]
public sealed class OntologyServiceTests
{
    [Fact]
    public async Task GetViewAsync_returns_view_with_knowledge_system_meta_for_admin()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "ontology-service-happy");
        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OntologyService>();

        var view = await service.GetViewAsync(ks.Id, actor, CancellationToken.None);

        Assert.NotNull(view);
        Assert.NotNull(view!.KnowledgeSystem);
        var ksMeta = Assert.IsType<KnowledgeSystemMeta>(view.KnowledgeSystem);
        Assert.Equal(ks.Id, ksMeta.Id);
        Assert.Equal(ks.Name, ksMeta.Name);
        Assert.Equal(ks.BaseIri, ksMeta.BaseIri);
        Assert.Null(ksMeta.Release);
        Assert.Equal(0, view.Stats.ClassCount);
    }

    [Fact]
    public async Task GetViewAsync_returns_null_when_KS_not_found()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OntologyService>();

        var view = await service.GetViewAsync(
            Guid.NewGuid(), actor, CancellationToken.None);

        Assert.Null(view);
    }

    [Fact]
    public async Task GetViewAsync_throws_for_non_viewer()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "ontology-service-norole");

        var otherUser = new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = "outsider",
            DisplayName = "Outsider",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("x", workFactor: 4),
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(otherUser);
        await db.SaveChangesAsync();

        var actor = new Actor(otherUser.Id.ToString());
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OntologyService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetViewAsync(ks.Id, actor, CancellationToken.None));
        Assert.Contains("Viewer access", ex.Message);
    }

    private static async Task SeedAdminAsync(AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername))
        {
            return;
        }
        db.Users.Add(new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = AuthTestWebApplicationFactory.AdminUsername,
            DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                AuthTestWebApplicationFactory.AdminPassword, workFactor: 4),
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<KnowledgeSystemEntity> CreateKsAsync(
        ISEStudioDbContext db, string tag)
    {
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("ks"),
            Id = Guid.NewGuid(),
            Name = $"ks-{tag}",
            Description = tag,
            OwnerId = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id,
            BaseIri = $"http://example.com/{tag}#",
            GraphIri = $"http://example.com/graph/{tag}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks;
    }
}