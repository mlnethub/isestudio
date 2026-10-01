using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISEStudio.Infrastructure.Persistence.Configurations;

public sealed class WorkspaceStatementEntityConfiguration : IEntityTypeConfiguration<WorkspaceStatementEntity>
{
    public void Configure(EntityTypeBuilder<WorkspaceStatementEntity> builder)
    {
        builder.ToTable("workspace_statements");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Layer).HasMaxLength(32).IsRequired();
        builder.Property(item => item.GraphIri).HasMaxLength(2048);
        builder.Property(item => item.Subject).HasMaxLength(2048).IsRequired();
        builder.Property(item => item.SubjectKind).HasMaxLength(16).IsRequired();
        builder.Property(item => item.Predicate).HasMaxLength(2048).IsRequired();
        builder.Property(item => item.Object).HasMaxLength(8192).IsRequired();
        builder.Property(item => item.ObjectKind).HasMaxLength(16).IsRequired();
        builder.Property(item => item.Language).HasMaxLength(32);
        builder.Property(item => item.Datatype).HasMaxLength(2048);
        builder.Property(item => item.CreatedAt).IsRequired();
        builder.HasIndex(item => new { item.KnowledgeSystemId, item.Layer });
    }
}
