using ISEStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Tests.Persistence;

namespace ISEStudio.Tests.Infrastructure.Persistence;

public sealed class AuthoritativeGraphSchemaTests
{
    [Fact]
    public void Authoritative_graph_model_exposes_iri_fact_and_release_statement_contract()
    {
        using var db = DbContextFactory.CreateSqlite();
        var model = db.Model;

        Assert.NotNull(model.FindEntityType("ISEStudio.Infrastructure.Persistence.Entities.EntityTypeEntity")
            ?.FindProperty(nameof(ISEStudio.Infrastructure.Persistence.Entities.EntityTypeEntity.Iri)));
        Assert.NotNull(model.FindEntityType("ISEStudio.Infrastructure.Persistence.Entities.RelationTypeEntity")
            ?.FindProperty(nameof(ISEStudio.Infrastructure.Persistence.Entities.RelationTypeEntity.Iri)));
        Assert.NotNull(model.FindEntityType("ISEStudio.Infrastructure.Persistence.Entities.GraphEntityEntity")
            ?.FindProperty(nameof(ISEStudio.Infrastructure.Persistence.Entities.GraphEntityEntity.Iri)));
        Assert.NotNull(model.FindEntityType("ISEStudio.Infrastructure.Persistence.Entities.FactEntity")
            ?.FindProperty(nameof(ISEStudio.Infrastructure.Persistence.Entities.FactEntity.ObjectValue)));
        Assert.NotNull(model.FindEntityType("ISEStudio.Infrastructure.Persistence.Entities.ReleaseStatementEntity"));
        Assert.NotNull(model.FindEntityType("ISEStudio.Infrastructure.Persistence.Entities.OntologyAxiomEntity"));

        Assert.Contains(model.GetEntityTypes(), entityType =>
            entityType.GetTableName() == "ontology_release_statements");
        Assert.Contains(model.GetEntityTypes(), entityType =>
            entityType.GetTableName() == "ontology_axioms");
    }

    [Fact]
    public void Fact_model_keeps_append_only_columns_and_active_indexes()
    {
        using var db = DbContextFactory.CreateSqlite();
        var fact = db.Model.FindEntityType(
            "ISEStudio.Infrastructure.Persistence.Entities.FactEntity")!;

        Assert.NotNull(fact.FindProperty(nameof(ISEStudio.Infrastructure.Persistence.Entities.FactEntity.InvalidatedAt)));
        Assert.NotNull(fact.FindProperty(nameof(ISEStudio.Infrastructure.Persistence.Entities.FactEntity.SupersedesFactId)));
        Assert.Contains(fact.GetIndexes(), index =>
            index.Properties.Any(property => property.Name == nameof(ISEStudio.Infrastructure.Persistence.Entities.FactEntity.SubjectEntityId)));
    }
}
