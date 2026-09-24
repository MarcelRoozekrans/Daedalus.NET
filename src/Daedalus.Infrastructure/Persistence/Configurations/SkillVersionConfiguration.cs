using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the <see cref="SkillVersion"/> aggregate (table <c>SkillVersions</c>). Insert-only:
///     nothing in this codebase updates or deletes a row here, so no concurrency token is configured.
/// </summary>
internal sealed class SkillVersionConfiguration : IEntityTypeConfiguration<SkillVersion>
{
    public void Configure(EntityTypeBuilder<SkillVersion> builder)
    {
        builder.ToTable("SkillVersions");

        // Composite key: a version is identified by which skill it belongs to and which content it captured.
        builder.HasKey(v => new { v.Name, v.ContentHash });

        builder.Property(v => v.Name)
            .IsRequired()
            .HasMaxLength(Skill.MaxNameLength);

        builder.Property(v => v.ContentHash)
            .IsRequired()
            .HasMaxLength(Skill.MaxContentHashLength);

        builder.Property(v => v.Description)
            .IsRequired()
            .HasMaxLength(Skill.MaxDescriptionLength);

        // Unbounded text column; the aggregate enforces Skill.MaxBodyLength so violations are validation errors.
        builder.Property(v => v.Body)
            .IsRequired()
            .HasColumnType("text");

        // Backing-field mapping: the private List<string> becomes a text[] column named Tags, same as SkillConfiguration.
        builder.Property("_tags")
            .HasColumnName("Tags")
            .HasColumnType("text[]")
            .IsRequired();
        builder.Ignore(v => v.Tags);

        builder.Property(v => v.SourcePath)
            .IsRequired()
            .HasMaxLength(Skill.MaxSourcePathLength);

        builder.Property(v => v.CreatedAt)
            .IsRequired();
    }
}
