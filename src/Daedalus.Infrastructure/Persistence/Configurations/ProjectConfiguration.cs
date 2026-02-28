using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Project = Daedalus.Domain.Entities.Project;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the Project aggregate root.
/// </summary>
internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> entity)
    {
        entity.HasKey(e => e.Id);

        entity.Property(e => e.ProjectName)
            .IsRequired()
            .HasMaxLength(256);

        entity.Property(e => e.Description)
            .IsRequired()
            .HasMaxLength(2000);

        entity.Property(e => e.Version)
            .IsRequired()
            .HasMaxLength(50);

        entity.Property(e => e.RepositoryUrl)
            .IsRequired()
            .HasMaxLength(2000)
            .HasDefaultValue(string.Empty);

        entity.Property(e => e.DefaultBranch)
            .IsRequired()
            .HasMaxLength(100)
            .HasDefaultValue("main");

        // Configure concurrency token for optimistic locking
        entity.Property(e => e.RowVersion)
            .IsRowVersion();

        entity.HasIndex(e => e.CreatedAt)
            .HasDatabaseName("IX_Project_CreatedAt");
    }
}
