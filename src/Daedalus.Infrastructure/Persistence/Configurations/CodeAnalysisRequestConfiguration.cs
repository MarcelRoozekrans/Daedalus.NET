using Daedalus.Domain.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the CodeAnalysisRequest aggregate root.
///     Configures owned value objects (RepositoryReference, CodeLocation, AnalysisOutcome)
///     with backward-compatible column names.
/// </summary>
internal sealed class CodeAnalysisRequestConfiguration : IEntityTypeConfiguration<CodeAnalysisRequest>
{
    public void Configure(EntityTypeBuilder<CodeAnalysisRequest> entity)
    {
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Title)
            .IsRequired()
            .HasMaxLength(255);

        entity.Property(e => e.Description)
            .IsRequired();

        // Repository reference value object -- preserves existing column names
        entity.OwnsOne(e => e.Repository, repo =>
        {
            repo.Property(r => r.Url)
                .HasColumnName("RepositoryUrl")
                .IsRequired()
                .HasMaxLength(1024);

            repo.Property(r => r.Branch)
                .HasColumnName("RepositoryBranch");

            repo.Property(r => r.CommitSha)
                .HasColumnName("CommitSha");

            repo.Property(r => r.Platform)
                .HasColumnName("Platform")
                .HasConversion<int>()
                .IsRequired();

            repo.HasIndex(r => r.Url)
                .HasDatabaseName("IX_CodeAnalysis_RepositoryUrl");
        });

        // Code location value object -- preserves existing column names
        entity.OwnsOne(e => e.Location, loc =>
        {
            loc.Property(l => l.FilePath)
                .HasColumnName("FilePath")
                .IsRequired()
                .HasMaxLength(1024);

            loc.Property(l => l.StartLine)
                .HasColumnName("StartLine");

            loc.Property(l => l.EndLine)
                .HasColumnName("EndLine");
        });

        // Analysis outcome value object -- preserves existing column names
        entity.OwnsOne(e => e.Outcome, outcome =>
        {
            outcome.Property(o => o.PullRequestUrl)
                .HasColumnName("PullRequestUrl");

            outcome.Property(o => o.CommitShaFinal)
                .HasColumnName("CommitShaFinal");

            outcome.Property(o => o.ValidationResult)
                .HasColumnName("ValidationResult")
                .HasColumnType("jsonb");

            outcome.Property(o => o.HasFailedValidation)
                .HasColumnName("HasFailedValidation");
        });

        // Ignore convenience properties (read-through to value objects)
        entity.Ignore(e => e.RepositoryUrl);
        entity.Ignore(e => e.RepositoryBranch);
        entity.Ignore(e => e.CommitSha);
        entity.Ignore(e => e.Platform);
        entity.Ignore(e => e.FilePath);
        entity.Ignore(e => e.StartLine);
        entity.Ignore(e => e.EndLine);
        entity.Ignore(e => e.PullRequestUrl);
        entity.Ignore(e => e.CommitShaFinal);
        entity.Ignore(e => e.ValidationResult);
        entity.Ignore(e => e.HasFailedValidation);

        entity.Property(e => e.Type)
            .HasConversion<int>()
            .IsRequired();

        entity.Property(e => e.Status)
            .HasConversion<int>()
            .IsRequired();

        entity.Property(e => e.Requirements)
            .HasColumnType("jsonb");

        entity.Property(e => e.ReferencedDocuments)
            .HasColumnType("jsonb");

        entity.Property(e => e.ExternalReferences)
            .HasColumnType("jsonb");

        // RowVersion for optimistic locking
        entity.Property(e => e.RowVersion)
            .IsRowVersion();

        entity.HasIndex(e => e.Status)
            .HasDatabaseName("IX_CodeAnalysis_Status");

        entity.HasIndex(e => e.Priority)
            .HasDatabaseName("IX_CodeAnalysis_Priority");

        entity.HasIndex(e => e.CreatedAt)
            .HasDatabaseName("IX_CodeAnalysis_CreatedAt");
    }
}
