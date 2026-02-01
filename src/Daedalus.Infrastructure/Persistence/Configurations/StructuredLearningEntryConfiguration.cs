using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the StructuredLearningEntry entity.
/// </summary>
internal sealed class StructuredLearningEntryConfiguration : IEntityTypeConfiguration<StructuredLearningEntry>
{
    public void Configure(EntityTypeBuilder<StructuredLearningEntry> entity)
    {
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Category)
            .HasConversion<int>()
            .IsRequired();

        entity.Property(e => e.Pattern)
            .IsRequired()
            .HasMaxLength(2000);

        entity.Property(e => e.Resolution)
            .IsRequired()
            .HasMaxLength(2000);

        entity.Property(e => e.Severity)
            .HasConversion<int>()
            .IsRequired();

        // Configure tags as PostgreSQL text array
        entity.Property("_tags")
            .HasColumnName("Tags")
            .HasColumnType("text[]");

        entity.HasIndex(e => e.Category)
            .HasDatabaseName("IX_StructuredLearning_Category");

        entity.HasIndex(e => e.Severity)
            .HasDatabaseName("IX_StructuredLearning_Severity");

        entity.HasIndex(e => e.ProjectId)
            .HasDatabaseName("IX_StructuredLearning_ProjectId");

        entity.HasIndex(e => e.SourceTaskId)
            .HasDatabaseName("IX_StructuredLearning_SourceTaskId");

        entity.HasIndex(e => e.CreatedAt)
            .HasDatabaseName("IX_StructuredLearning_CreatedAt");

        entity.HasIndex(e => e.HitCount)
            .HasDatabaseName("IX_StructuredLearning_HitCount");
    }
}
