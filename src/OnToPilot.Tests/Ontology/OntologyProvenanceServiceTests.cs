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
public sealed class OntologyProvenanceServiceTests
{
    [Fact]
    public async Task ListSourcesAsync_aggregates_per_document_sorted_by_axiom_count_desc()
    {
        await using var app = new AuthTestWebApplicationFactory();
        await SeedAdminAsync(app);
        var db = app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "prov-sources");
        var doc1 = await CreateDocumentAsync(db, ks.Id, "manual.pdf", "/manuals");
        var doc2 = await CreateDocumentAsync(db, ks.Id, "spec.docx", "/");
        var chunk1 = await CreateChunkAsync(db, doc1.Id);
        var chunk2 = await CreateChunkAsync(db, doc2.Id);
        // doc1 贡献 3 个不同 axiom(chunk1),doc2 贡献 1 个(chunk2)
        AddAxiom(db, ks.Id, "subClassOf|Pump|Device", chunk1.Id);
        AddAxiom(db, ks.Id, "subClassOf|Valve|Device", chunk1.Id);
        AddAxiom(db, ks.Id, "domain|hasFlow|Pump", chunk1.Id);
        AddAxiom(db, ks.Id, "subClassOf|Sensor|Device", chunk2.Id);
        await db.SaveChangesAsync();

        var actor = new Actor(admin.Id.ToString());
        using var scope = app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<OntologyProvenanceService>();

        var rows = await svc.ListSourcesAsync(ks.Id, actor, CancellationToken.None);

        Assert.NotNull(rows);
        Assert.Equal(2, rows!.Count);
        // doc1(axiom_count=3)在前,doc2(=1)在后
        Assert.Equal(doc1.Id, rows[0].DocumentId);
        Assert.Equal(3, rows[0].AxiomCount);
        Assert.Equal(1, rows[0].ChunkCount);
        Assert.True(rows[0].Exists);
        Assert.Equal("manual.pdf", rows[0].Filename);
        Assert.Equal("/manuals", rows[0].Folder);
        Assert.Equal(doc2.Id, rows[1].DocumentId);
        Assert.Equal(1, rows[1].AxiomCount);
    }

    // 后续 Task 复用的 seed 辅助方法定义在本文件底部(Task 2/3 补充更多用例时共用)
    private static async Task SeedAdminAsync(AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername)) return;
        db.Users.Add(new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
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
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks;
    }

    private static async Task<DocumentEntity> CreateDocumentAsync(OnToPilotDbContext db, Guid ksId, string name, string folder)
    {
        var d = new DocumentEntity
        {
            LegacyId = TestLegacyIds.Next("document"), Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId, OriginalFilename = name, Folder = folder,
            Sha256 = Guid.NewGuid().ToString("N"), Ext = "pdf", SizeBytes = 1,
            StoragePath = "aa/bb/x", UploadedAt = DateTimeOffset.UtcNow, ParseStatus = "parsed",
        };
        db.Documents.Add(d); await db.SaveChangesAsync(); return d;
    }

    private static async Task<ChunkEntity> CreateChunkAsync(OnToPilotDbContext db, Guid docId)
    {
        var c = new ChunkEntity
        {
            LegacyId = TestLegacyIds.Next("chunk"), Id = Guid.NewGuid(),
            DocumentId = docId, Idx = 0, Text = "t", CharStart = 0, CharEnd = 1,
            TokenEstimate = 1, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Chunks.Add(c); await db.SaveChangesAsync(); return c;
    }

    private static void AddAxiom(OnToPilotDbContext db, Guid ksId, string key, Guid? chunkId, Guid? jobId = null)
    {
        db.AxiomProvenances.Add(new AxiomProvenanceEntity
        {
            LegacyId = TestLegacyIds.Next("axiomprov"), Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId, AxiomKey = key, ChunkId = chunkId, JobId = jobId,
            Method = "extraction", ActorName = "admin", CreatedAt = DateTimeOffset.UtcNow,
        });
    }
}
