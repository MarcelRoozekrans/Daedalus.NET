using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the <see cref="RoleCharterVersion"/> aggregate (table <c>RoleCharterVersions</c>).
///     Insert-only: nothing in this codebase updates or deletes a row here, so no concurrency token is configured.
/// </summary>
internal sealed class RoleCharterVersionConfiguration : IEntityTypeConfiguration<RoleCharterVersion>
{
    public void Configure(EntityTypeBuilder<RoleCharterVersion> builder)
    {
        builder.ToTable("RoleCharterVersions");

        // Composite key: a version is identified by which role it belongs to and which content it captured.
        builder.HasKey(v => new { v.Role, v.ContentHash });

        builder.Property(v => v.Role)
            .IsRequired()
            .HasMaxLength(RoleCharterVersion.MaxRoleLength);

        builder.Property(v => v.ContentHash)
            .IsRequired()
            .HasMaxLength(RoleCharterVersion.MaxContentHashLength);

        builder.Property(v => v.Description)
            .IsRequired()
            .HasMaxLength(RoleCharterVersion.MaxDescriptionLength);

        // Unbounded text column; the charter's body has no length limit of its own in Thalos.NET.Skills.Charters.
        builder.Property(v => v.Instructions)
            .IsRequired()
            .HasColumnType("text");

        builder.Property(v => v.Model)
            .HasMaxLength(RoleCharterVersion.MaxModelLength);

        // Backing-field mapping: the private List<string> becomes a text[] column named Skills.
        builder.Property("_skills")
            .HasColumnName("Skills")
            .HasColumnType("text[]")
            .IsRequired();
        builder.Ignore(v => v.Skills);

        builder.Property(v => v.SourcePath)
            .IsRequired()
            .HasMaxLength(RoleCharterVersion.MaxSourcePathLength);

        builder.Property(v => v.UpdatedAt)
            .IsRequired();
    }
}
