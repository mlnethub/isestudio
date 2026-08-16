using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Configurations;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Tests.Persistence;

/// <summary>
/// Round-trips JSON content through SQLite via <see cref="JsonStringValueConverter"/>.
/// Verifies that:
///  - a <see cref="JsonDocument"/> property survives a Save+Reload cycle;
///  - nulls are preserved as nulls;
///  - complex nested JSON (arrays + objects + scalars) is lossless;
///  - the converter is provider-agnostic (exercised through SQLite, the same
///    pipeline Postgres uses with the column type promoted to <c>jsonb</c>).
/// </summary>
public sealed class JsonStringValueConverterTests
{
    [Fact]
    public void Null_round_trips_through_converter()
    {
        using var db = DbContextFactory.CreateSqlite();
        var ksId = SeedKnowledgeSystem(db);
        db.ExtractionJobs.Add(new ExtractionJobEntity
        {
            KnowledgeSystemId = ksId,
            UnknownClasses = null,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var stored = db.ExtractionJobs.Single().UnknownClasses;
        Assert.Null(stored);
    }

    [Fact]
    public void Nested_object_round_trips_losslessly_through_sqlite()
    {
        using var db = DbContextFactory.CreateSqlite();
        var ksId = SeedKnowledgeSystem(db);

        using var doc = JsonDocument.Parse("""
            {
              "name": "deepseek/deepseek-chat",
              "params": { "temperature": 0.2, "top_p": 0.95 },
              "tags": ["extract", "tbox", "fast"]
            }
            """);
        // Clone into a fresh JsonDocument so the original can be disposed
        // without affecting the stored value.
        var payload = JsonDocument.Parse(doc.RootElement.GetRawText());
        db.ExtractionJobs.Add(new ExtractionJobEntity
        {
            KnowledgeSystemId = ksId,
            UnknownClasses = payload,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var stored = db.ExtractionJobs.Single().UnknownClasses;
        Assert.NotNull(stored);

        using var parsed = JsonDocument.Parse(stored!.RootElement.GetRawText());
        Assert.Equal("deepseek/deepseek-chat", parsed.RootElement.GetProperty("name").GetString());
        Assert.Equal(0.2, parsed.RootElement.GetProperty("params").GetProperty("temperature").GetDouble());
        Assert.Equal("fast", parsed.RootElement.GetProperty("tags")[2].GetString());
    }

    [Fact]
    public void Empty_object_round_trips_through_converter()
    {
        using var db = DbContextFactory.CreateSqlite();
        var ksId = SeedKnowledgeSystem(db);

        using var doc = JsonDocument.Parse("{}");
        db.ExtractionJobs.Add(new ExtractionJobEntity
        {
            KnowledgeSystemId = ksId,
            UnknownClasses = JsonDocument.Parse(doc.RootElement.GetRawText()),
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var stored = db.ExtractionJobs.Single().UnknownClasses;
        Assert.NotNull(stored);
        Assert.Equal(JsonValueKind.Object, stored!.RootElement.ValueKind);
    }

    /// <summary>
    /// Insert a parent KnowledgeSystem so the FK constraint on
    /// <see cref="ExtractionJobEntity.KnowledgeSystemId"/> is satisfied.
    /// </summary>
    private static Guid SeedKnowledgeSystem(OnToPilotDbContext db)
    {
        var ksId = Guid.NewGuid();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = ksId,
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "probe-ks",
        });
        db.SaveChanges();
        return ksId;
    }
}