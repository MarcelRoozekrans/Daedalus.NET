using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Project = Daedalus.Domain.Entities.Project;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the Project aggregate root. The project repository and query service read and write
///     through ZeroAlloc.ORM; this mapping stays only for <c>CostAnalyticsService</c>, which still joins the table
///     through EF Core until it is ported. <see cref="Daedalus.Domain.Entities.Project.RowVersion"/> is the <c>xmin</c>
///     ZeroAlloc.ORM reads, so EF ignores it, and the dead <c>bytea</c> <c>"RowVersion"</c> column is not mapped.
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

        entity.Ignore(e => e.RowVersion);

        entity.HasIndex(e => e.CreatedAt)
            .HasDatabaseName("IX_Project_CreatedAt");
    }
}
