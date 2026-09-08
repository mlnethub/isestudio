using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISEStudio.Infrastructure.Persistence.Configurations;

public sealed class OntologyAxiomEntityConfiguration : IEntityTypeConfiguration<OntologyAxiomEntity>
{
    public void Configure(EntityTypeBuilder<OntologyAxiomEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("ontology_axioms");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.KnowledgeSystemId).HasColumnName("knowledge_system_id");
        builder.Property(x => x.SubjectIri).HasColumnName("subject_iri").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.PredicateIri).HasColumnName("predicate_iri").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.ObjectIri).HasColumnName("object_iri").HasMaxLength(2048);
        builder.Property(x => x.ObjectValue).HasColumnName("object_value");
        builder.Property(x => x.Payload).HasColumnName("payload");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.HasIndex(x => new { x.KnowledgeSystemId, x.SubjectIri, x.PredicateIri })
            .HasDatabaseName("ix_ontology_axioms_knowledge_system_id_subject_predicate");
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ReleaseStatementEntityConfiguration : IEntityTypeConfiguration<ReleaseStatementEntity>
{
    public void Configure(EntityTypeBuilder<ReleaseStatementEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("ontology_release_statements");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.KnowledgeSystemId).HasColumnName("knowledge_system_id");
        builder.Property(x => x.ReleaseId).HasColumnName("release_id");
        builder.Property(x => x.Layer).HasColumnName("layer").HasMaxLength(32).IsRequired();
        builder.Property(x => x.GraphIri).HasColumnName("graph_iri").HasMaxLength(2048);
        builder.Property(x => x.SubjectIri).HasColumnName("subject_iri").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.PredicateIri).HasColumnName("predicate_iri").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.ObjectIri).HasColumnName("object_iri").HasMaxLength(2048);
        builder.Property(x => x.ObjectValue).HasColumnName("object_value");
        builder.Property(x => x.StatementHash).HasColumnName("statement_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Payload).HasColumnName("payload");
        builder.HasIndex(x => new { x.ReleaseId, x.StatementHash })
            .IsUnique()
            .HasDatabaseName("ux_ontology_release_statements_release_id_statement_hash");
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<OntologyReleaseEntity>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Cascade);
    }
}