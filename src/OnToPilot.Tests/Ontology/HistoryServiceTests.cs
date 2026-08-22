using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Application.Foundation;
using OnToPilot.Authorization;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Extraction;
using OnToPilot.Tests.Persistence;
using Xunit;

namespace OnToPilot.Tests.Ontology;

[Collection(nameof(ExtractionTestCollection))]
public sealed class HistoryServiceTests
{
    [Fact]
    public async Task ListHistoryAsync_paginates_filtered_and_omits_binary_diffs()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "history-list");
        AddAudit(db, ks.Id, admin.Id, "ontology.edit", "Added Pump class", graph: ks.GraphIri, added: System.Text.Encoding.UTF8.GetBytes("<urn:Pump> a <urn:C> <urn:g> .\n"), actorName: admin.DisplayName);
        AddAudit(db, ks.Id, admin.Id, "abox.resolve", "Resolved valve individual", graph: ks.GraphIri + "/abox", actorName: admin.DisplayName);
        AddAudit(db, ks.Id, admin.Id, "conflict.resolve", "Resolved a conflict", graph: ks.GraphIri, actorName: admin.DisplayName);
        await db.SaveChangesAsync();

        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<HistoryService>();

        var res = await svc.ListHistoryAsync(ks.Id, actor, "ontology", null, 50, 0, CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(1, res!.Total);
        Assert.Single(res.Items);
        var item = res.Items[0];
        Assert.Equal("ontology.edit", item.Action);
        Assert.Equal("Added Pump class", item.Summary);
        Assert.True(item.CanRollback);
        Assert.Equal(admin.DisplayName, item.ActorName);
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

    private static async Task<KnowledgeSystemEntity> CreateKsAsync(OnToPilotDbContext db, string tag)
    {
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("ks"), Id = Guid.NewGuid(),
            Name = $"ks-{tag}", Description = tag,
            OwnerId = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id,
            BaseIri = $"http://example.com/{tag}#", GraphIri = $"http://example.com/graph/{tag}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks); await db.SaveChangesAsync(); return ks;
    }

    private static void AddAudit(OnToPilotDbContext db, Guid ksId, Guid actorId, string action, string summary,
        string? graph = null, byte[]? added = null, byte[]? removed = null, string? groupId = null, string actorName = "Admin")
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            LegacyId = TestLegacyIds.Next("audit_event"), Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId, ActorId = actorId, ActorName = actorName,
            Action = action, Summary = summary, Graph = graph, GroupId = groupId,
            Added = added, Removed = removed, CreatedAt = DateTimeOffset.UtcNow,
        });
    }
}
