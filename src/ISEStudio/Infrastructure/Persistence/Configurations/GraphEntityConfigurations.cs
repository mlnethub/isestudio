using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISEStudio.Infrastructure.Persistence.Configurations;

public sealed class EntityTypeEntityConfiguration : IEntityTypeConfiguration<EntityTypeEntity>
{
    public void Configure(EntityTypeBuilder<EntityTypeEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("entity_types");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.KnowledgeSystemId).HasColumnName("knowledge_system_id");
        builder.Property(x => x.Iri).HasColumnName("iri").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Key).HasColumnName("key").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Label).HasColumnName("label").HasMaxLength(255);
        builder.Property(x => x.Description).HasColumnName("description");

        builder.HasIndex(x => new { x.KnowledgeSystemId, x.Key })
            .IsUnique()
            .HasDatabaseName("ux_entity_types_knowledge_system_id_key");

        builder.HasIndex(x => new { x.KnowledgeSystemId, x.Iri })
            .IsUnique()
            .HasDatabaseName("ux_entity_types_knowledge_system_id_iri");

        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RelationTypeEntityConfiguration : IEntityTypeConfiguration<RelationTypeEntity>
{
    public void Configure(EntityTypeBuilder<RelationTypeEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("relation_types");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.KnowledgeSystemId).HasColumnName("knowledge_system_id");
        builder.Property(x => x.Iri).HasColumnName("iri").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Key).HasColumnName("key").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Label).HasColumnName("label").HasMaxLength(255);
        builder.Property(x => x.Description).HasColumnName("description");

        builder.HasIndex(x => new { x.KnowledgeSystemId, x.Key })
            .IsUnique()
            .HasDatabaseName("ux_relation_types_knowledge_system_id_key");

        builder.HasIndex(x => new { x.KnowledgeSystemId, x.Iri })
            .IsUnique()
            .HasDatabaseName("ux_relation_types_knowledge_system_id_iri");

        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RelationTypeDomainEntityConfiguration : IEntityTypeConfiguration<RelationTypeDomainEntity>
{
    public void Configure(EntityTypeBuilder<RelationTypeDomainEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("relation_type_domains");

        builder.HasKey(x => new { x.RelationTypeId, x.EntityTypeId });
        builder.Property(x => x.RelationTypeId).HasColumnName("relation_type_id");
        builder.Property(x => x.EntityTypeId).HasColumnName("entity_type_id");

        builder.HasOne<RelationTypeEntity>().WithMany().HasForeignKey(x => x.RelationTypeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<EntityTypeEntity>().WithMany().HasForeignKey(x => x.EntityTypeId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RelationTypeRangeEntityConfiguration : IEntityTypeConfiguration<RelationTypeRangeEntity>
{
    public void Configure(EntityTypeBuilder<RelationTypeRangeEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("relation_type_ranges");

        builder.HasKey(x => new { x.RelationTypeId, x.EntityTypeId });
        builder.Property(x => x.RelationTypeId).HasColumnName("relation_type_id");
        builder.Property(x => x.EntityTypeId).HasColumnName("entity_type_id");

        builder.HasOne<RelationTypeEntity>().WithMany().HasForeignKey(x => x.RelationTypeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<EntityTypeEntity>().WithMany().HasForeignKey(x => x.EntityTypeId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EntityTypeParentEntityConfiguration : IEntityTypeConfiguration<EntityTypeParentEntity>
{
    public void Configure(EntityTypeBuilder<EntityTypeParentEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("entity_type_parents");

        builder.HasKey(x => new { x.EntityTypeId, x.ParentEntityTypeId });
        builder.Property(x => x.EntityTypeId).HasColumnName("entity_type_id");
        builder.Property(x => x.ParentEntityTypeId).HasColumnName("parent_entity_type_id");

        builder.HasOne<EntityTypeEntity>().WithMany().HasForeignKey(x => x.EntityTypeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<EntityTypeEntity>().WithMany().HasForeignKey(x => x.ParentEntityTypeId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class GraphEntityEntityConfiguration : IEntityTypeConfiguration<GraphEntityEntity>
{
    public void Configure(EntityTypeBuilder<GraphEntityEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("graph_entities");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.KnowledgeSystemId).HasColumnName("knowledge_system_id");
        builder.Property(x => x.Iri).HasColumnName("iri").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.EntityTypeId).HasColumnName("entity_type_id");
        builder.Property(x => x.Label).HasColumnName("label").HasMaxLength(255);
        builder.Property(x => x.Description).HasColumnName("description");

        builder.HasIndex(x => x.KnowledgeSystemId)
            .HasDatabaseName("ix_graph_entities_knowledge_system_id");

        builder.HasIndex(x => new { x.KnowledgeSystemId, x.Iri })
            .IsUnique()
            .HasDatabaseName("ux_graph_entities_knowledge_system_id_iri");

        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<EntityTypeEntity>().WithMany().HasForeignKey(x => x.EntityTypeId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FactEntityConfiguration : IEntityTypeConfiguration<FactEntity>
{
    public void Configure(EntityTypeBuilder<FactEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable(
            "facts",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint("ck_facts_exactly_one_object", "((object_entity_id IS NULL) <> (object_value IS NULL))");
                tableBuilder.HasCheckConstraint("ck_facts_confidence_unit_interval", "(confidence >= 0 AND confidence <= 1)");
                tableBuilder.HasCheckConstraint("ck_facts_valid_window", "(valid_to IS NULL OR valid_from IS NULL OR valid_from <= valid_to)");
            });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.KnowledgeSystemId).HasColumnName("knowledge_system_id");
        builder.Property(x => x.SubjectEntityId).HasColumnName("subject_entity_id");
        builder.Property(x => x.PredicateId).HasColumnName("predicate_id");
        builder.Property(x => x.ObjectEntityId).HasColumnName("object_entity_id");
        builder.Property(x => x.ObjectValue).HasColumnName("object_value");
        builder.Property(x => x.Confidence).HasColumnName("confidence");
        builder.Property(x => x.ValidFrom).HasColumnName("valid_from");
        builder.Property(x => x.ValidTo).HasColumnName("valid_to");
        builder.Property(x => x.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
        builder.Property(x => x.InvalidatedAt).HasColumnName("invalidated_at");
        builder.Property(x => x.SupersedesFactId).HasColumnName("supersedes_fact_id");

        builder.HasIndex(x => new { x.KnowledgeSystemId, x.SubjectEntityId })
            .HasFilter("invalidated_at IS NULL")
            .HasDatabaseName("ix_facts_knowledge_system_id_subject_entity_id_active");

        builder.HasIndex(x => new { x.KnowledgeSystemId, x.ObjectEntityId })
            .HasFilter("invalidated_at IS NULL")
            .HasDatabaseName("ix_facts_knowledge_system_id_object_entity_id_active");

        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GraphEntityEntity>().WithMany().HasForeignKey(x => x.SubjectEntityId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<RelationTypeEntity>().WithMany().HasForeignKey(x => x.PredicateId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GraphEntityEntity>().WithMany().HasForeignKey(x => x.ObjectEntityId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<FactEntity>().WithMany().HasForeignKey(x => x.SupersedesFactId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class FactEvidenceEntityConfiguration : IEntityTypeConfiguration<FactEvidenceEntity>
{
    public void Configure(EntityTypeBuilder<FactEvidenceEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("fact_evidence");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.FactId).HasColumnName("fact_id");
        builder.Property(x => x.SourceChunkId).HasColumnName("source_chunk_id");
        builder.Property(x => x.Quote).HasColumnName("quote").IsRequired();
        builder.Property(x => x.Predicate).HasColumnName("predicate").HasMaxLength(255).IsRequired();

        builder.HasIndex(x => x.FactId)
            .HasDatabaseName("ix_fact_evidence_fact_id");

        builder.HasOne<FactEntity>().WithMany().HasForeignKey(x => x.FactId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ChunkEntity>().WithMany().HasForeignKey(x => x.SourceChunkId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FactConflictEntityConfiguration : IEntityTypeConfiguration<FactConflictEntity>
{
    public void Configure(EntityTypeBuilder<FactConflictEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("fact_conflicts");

        builder.HasKey(x => new { x.FactId, x.ConflictId });
        builder.Property(x => x.FactId).HasColumnName("fact_id");
        builder.Property(x => x.ConflictId).HasColumnName("conflict_id");

        builder.HasOne<FactEntity>().WithMany().HasForeignKey(x => x.FactId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ConflictEntity>().WithMany().HasForeignKey(x => x.ConflictId).OnDelete(DeleteBehavior.Cascade);
    }
}