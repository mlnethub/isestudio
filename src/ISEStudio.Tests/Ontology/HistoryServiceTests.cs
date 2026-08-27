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

    [Fact]
    public async Task RollbackAsync_inverts_single_graph_event_and_records_audit()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "history-rollback");
        // 在 live store 建一条 TBox 三元,再记一条 added-only 的 audit(回滚应移除它)
        var gName = new Oxigraph.NamedNode(ks.GraphIri);
        var store = app.Services.GetRequiredService<StoreWrapper>();
        store.AddQuads(gName, new[] { new Oxigraph.Quad(
            new Oxigraph.NamedNode("urn:Pump"), new Oxigraph.NamedNode("urn:type"),
            new Oxigraph.NamedNode("urn:Class"), gName) });
        var addedBlob = store.DumpNQuads(gName);  // raw N-Quads(含该三元)
        AddAudit(db, ks.Id, admin.Id, "ontology.edit", "added Pump", graph: ks.GraphIri, added: addedBlob, actorName: admin.DisplayName);
        await db.SaveChangesAsync();

        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<HistoryService>();
        var eventId = db.AuditEvents.AsNoTracking().First(e => e.KnowledgeSystemId == ks.Id).Id;

        var res = await svc.RollbackAsync(ks.Id, eventId, actor, CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(1, res!.Undone);
        // 回滚后该三元已移除
        Assert.Empty(store.Match(subjectIri: "urn:Pump", predicateIri: "urn:type", objectIri: "urn:Class", graphIri: ks.GraphIri));
        // 记了一条 system.rollback audit
        Assert.True(db.AuditEvents.AsNoTracking().Any(e => e.Action == "system.rollback" && e.KnowledgeSystemId == ks.Id));
    }

    [Fact]
    public async Task RollbackAsync_throws_400_when_event_has_no_diff()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "history-nodiff");
        AddAudit(db, ks.Id, admin.Id, "system.note", "a no-op note", graph: ks.GraphIri); // added/removed = null
        await db.SaveChangesAsync();
        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<HistoryService>();
        var eventId = db.AuditEvents.AsNoTracking().First(e => e.KnowledgeSystemId == ks.Id).Id;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RollbackAsync(ks.Id, eventId, actor, CancellationToken.None));
        Assert.Contains("nothing to roll back", ex.Message);
    }

    [Fact]
    public async Task RollbackAsync_throws_404_when_event_not_found()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "history-404");
        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<HistoryService>();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => svc.RollbackAsync(ks.Id, Guid.NewGuid(), actor, CancellationToken.None));
    }

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

    private static async Task<KnowledgeSystemEntity> CreateKsAsync(ISEStudioDbContext db, string tag)
    {
        var ks = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            Name = $"ks-{tag}", Description = tag,
            OwnerId = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id,
            BaseIri = $"http://example.com/{tag}#", GraphIri = $"http://example.com/graph/{tag}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks); await db.SaveChangesAsync(); return ks;
    }

    private static void AddAudit(ISEStudioDbContext db, Guid ksId, Guid actorId, string action, string summary,
        string? graph = null, byte[]? added = null, byte[]? removed = null, string? groupId = null, string actorName = "Admin")
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId, ActorId = actorId, ActorName = actorName,
            Action = action, Summary = summary, Graph = graph, GroupId = groupId,
            Added = added, Removed = removed, CreatedAt = DateTimeOffset.UtcNow,
        });
    }
}
