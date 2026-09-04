using Npgsql;

namespace ISEStudio.IntegrationTests.Graph;

public sealed class GraphSchemaTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;

    public GraphSchemaTests(PostgresGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Graph_migration_creates_fact_ledger_constraints()
    {
        var tables = await _fixture.GetTableNamesAsync();
        Assert.Contains("facts", tables);
        Assert.Contains("fact_evidence", tables);

        await _fixture.SeedGraphReferencesAsync();

        await using var connection = await _fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO facts (id, knowledge_system_id, subject_entity_id, predicate_id,
                               object_entity_id, object_value, confidence, recorded_at)
            VALUES (@id, @ks, @subject, @predicate,
                    @object, '{""bad"":true}'::jsonb, 0.8, now())
        ";

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("ks", _fixture.KnowledgeSystemId);
        command.Parameters.AddWithValue("subject", _fixture.SubjectEntityId);
        command.Parameters.AddWithValue("predicate", _fixture.PredicateId);
        command.Parameters.AddWithValue("object", _fixture.ObjectEntityId);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task Graph_migration_uses_jsonb_for_fact_object_value()
    {
        var columns = await _fixture.GetColumnsAsync("facts");
        Assert.Contains(("object_value", "jsonb"), columns);
    }

    [Fact]
    public async Task Graph_migration_creates_active_fact_lookup_indexes()
    {
        var indexes = await _fixture.GetIndexDefinitionsAsync("facts", "fact_evidence");

        Assert.Contains("ix_facts_knowledge_system_id_subject_entity_id_active", indexes.Keys);
        Assert.Contains("ix_facts_knowledge_system_id_object_entity_id_active", indexes.Keys);
        Assert.Contains("ix_fact_evidence_fact_id", indexes.Keys);

        Assert.Contains("WHERE (invalidated_at IS NULL)", indexes["ix_facts_knowledge_system_id_subject_entity_id_active"]);
        Assert.Contains("WHERE (invalidated_at IS NULL)", indexes["ix_facts_knowledge_system_id_object_entity_id_active"]);
        Assert.Contains("(fact_id)", indexes["ix_fact_evidence_fact_id"]);
    }
}