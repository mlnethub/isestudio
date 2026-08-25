using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Application.Foundation;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Xunit;

namespace ISEStudio.Tests.Ontology;

[Collection(nameof(ExtractionTestCollection))]
public sealed class PublishedOntologyServiceTests
{
    [Fact]
    public async Task GetViewAsync_returns_view_with_release_version_for_active_release()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);

        var publicId = "pub-" + Guid.NewGuid().ToString("N")[..8];
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("ks"),
            Id = Guid.NewGuid(),
            PublicId = publicId,
            Name = "ks-pub-test",
            Description = "",
            OwnerId = admin.Id,
            BaseIri = "http://example.com/pub#",
            GraphIri = "http://example.com/graph/pub",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);

        var releaseId = Guid.NewGuid();
        var release = new OntologyReleaseEntity
        {
            Id = releaseId,
            KnowledgeSystemId = ks.Id,
            Version = "1.0.0",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.OntologyReleases.Add(release);
        await db.SaveChangesAsync();

        var rdfRoot = app.Services
            .GetRequiredService<IConfiguration>()["ISEStudio:Storage:RdfRoot"]
            ?? "./data/rdf";
        var shardStore = new ReleaseArtifactStore(System.IO.Path.Combine(rdfRoot, "releases"));
        shardStore.Write(releaseId.ToString(), RdfLayer.TBox,
            System.Text.Encoding.UTF8.GetBytes(
                "<urn:Animal> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> " +
                "<http://www.w3.org/2002/07/owl#Class> <http://example.com/graph/pub> .\n"));

        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<PublishedOntologyService>();

        var view = await service.GetViewAsync(publicId, "1.0.0", actor, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Single(view!.Classes);
        Assert.Equal("urn:Animal", view.Classes[0].Iri);
        Assert.NotNull(view.KnowledgeSystem);
        var ksMeta = Assert.IsType<KnowledgeSystemMeta>(view.KnowledgeSystem);
        Assert.Equal("1.0.0", ksMeta.Release);
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