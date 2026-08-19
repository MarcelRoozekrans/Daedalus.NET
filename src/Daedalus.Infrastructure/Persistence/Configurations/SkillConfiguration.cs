using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the <see cref="Skill"/> aggregate (table <c>Skills</c>, the Thalos skill store).
///     Documents only — embeddings live in the in-process skill index, which is rebuilt from these rows at startup.
/// </summary>
internal sealed class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> builder)
    {
        builder.ToTable("Skills");

        // The name is the primary key: skill documents carry no surrogate id and names are unique by construction
        // (a duplicate across roots is a load error, never a silent rename).
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasColumnName("Name")
            .IsRequired()
            .HasMaxLength(Skill.MaxNameLength);

        builder.Property(s => s.Description)
            .IsRequired()
            .HasMaxLength(Skill.MaxDescriptionLength);

        // Unbounded text column; the aggregate enforces MaxBodyLength so violations are validation errors.
        builder.Property(s => s.Body)
            .IsRequired()
            .HasColumnType("text");

        // Backing-field mapping: the private List<string> becomes a text[] column named Tags.
        builder.Property("_tags")
            .HasColumnName("Tags")
            .HasColumnType("text[]")
            .IsRequired();
        builder.Ignore(s => s.Tags);

        builder.Property(s => s.SourcePath)
            .IsRequired()
            .HasMaxLength(Skill.MaxSourcePathLength);

        builder.Property(s => s.ContentHash)
            .IsRequired()
            .HasMaxLength(Skill.MaxContentHashLength);

        // Every catalogue and list query filters on IsActive; the table is repo-sized, so this is the only index it needs.
        builder.HasIndex(s => s.IsActive)
            .HasDatabaseName("IX_Skill_IsActive");
    }
}
