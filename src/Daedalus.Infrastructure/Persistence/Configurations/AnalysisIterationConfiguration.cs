using Daedalus.Domain.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the AnalysisIteration entity.
/// </summary>
internal sealed class AnalysisIterationConfiguration : IEntityTypeConfiguration<AnalysisIteration>
{
    public void Configure(EntityTypeBuilder<AnalysisIteration> entity)
    {
        entity.HasKey(e => e.Id);

        entity.Property(e => e.CodeAnalysisRequestId)
            .IsRequired();

        entity.Property(e => e.IterationNumber)
            .IsRequired();

        entity.Property(e => e.PromptSent)
            .IsRequired();

        entity.Property(e => e.AiResponse)
            .IsRequired();

        entity.Property(e => e.ValidationErrors)
            .HasColumnType("jsonb");

        entity.Property(e => e.InputTokens)
            .HasDefaultValue(0);

        entity.Property(e => e.OutputTokens)
            .HasDefaultValue(0);

        entity.Property(e => e.ModelId)
            .HasMaxLength(100);

        entity.HasIndex(e => e.CodeAnalysisRequestId)
            .HasDatabaseName("IX_AnalysisIteration_RequestId");

        entity.HasIndex(e => e.CreatedAt)
            .HasDatabaseName("IX_AnalysisIteration_CreatedAt");
    }
}
