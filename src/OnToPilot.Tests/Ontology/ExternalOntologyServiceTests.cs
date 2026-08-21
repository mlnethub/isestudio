using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Application.Foundation;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Extraction;
using OnToPilot.Tests.Persistence;
using Xunit;

namespace OnToPilot.Tests.Ontology;

[Collection(nameof(ExtractionTestCollection))]
public sealed class ExternalOntologyServiceTests
{
    [Fact]
    public async Task GetViewAsync_returns_view_with_public_id_meta()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var publicId = "ext-" + Guid.NewGuid().ToString("N")[..8];
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("ks"),
            Id = Guid.NewGuid(),
            PublicId = publicId,
            Name = "ks-ext-test",
            Description = "",
            OwnerId = admin.Id,
            BaseIri = "http://example.com/ext#",
            GraphIri = "http://example.com/graph/ext",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();

        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ExternalOntologyService>();

        var view = await service.GetViewAsync(publicId, actor, CancellationToken.None);

        Assert.NotNull(view);
        var meta = Assert.IsType<ExternalKnowledgeSystemMeta>(view!.KnowledgeSystem);
        Assert.Equal(publicId, meta.PublicId);
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
}