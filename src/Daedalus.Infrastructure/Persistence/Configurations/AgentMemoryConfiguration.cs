using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the <see cref="AgentMemory"/> aggregate (table <c>AgentMemories</c>, the Thalos memory store).
///     Records only — vectors live in the Rag.NET index (<c>rag_chunks</c>).
/// </summary>
internal sealed class AgentMemoryConfiguration : IEntityTypeConfiguration<AgentMemory>
{
    public void Configure(EntityTypeBuilder<AgentMemory> builder)
    {
        builder.ToTable("AgentMemories");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.OwnerId)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(m => m.AgentId);

        builder.Property(m => m.Kind)
            .IsRequired()
            .HasMaxLength(AgentMemory.MaxKindLength);

        // Unbounded text column (design §6); the aggregate enforces MaxTextLength so violations are validation errors.
        builder.Property(m => m.Text)
            .IsRequired()
            .HasColumnType("text");

        // Backing-field mapping: the private List<string> becomes a text[] column named Tags.
        builder.Property("_tags")
            .HasColumnName("Tags")
            .HasColumnType("text[]")
            .IsRequired();
        builder.Ignore(m => m.Tags);

        builder.Property(m => m.Source)
            .IsRequired()
            .HasMaxLength(AgentMemory.MaxSourceLength);

        builder.Property(m => m.Importance).IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.UpdatedAt).IsRequired();
        builder.Property(m => m.LastRecalledAt);
        builder.Property(m => m.RecallCount).IsRequired();
        builder.Property(m => m.IsArchived).IsRequired();
        builder.Property(m => m.IndexPending).IsRequired();

        builder.HasIndex(m => new { m.OwnerId, m.AgentId })
            .HasDatabaseName("IX_AgentMemory_Owner_Agent");

        builder.HasIndex(m => new { m.OwnerId, m.Kind })
            .HasDatabaseName("IX_AgentMemory_Owner_Kind");

        builder.HasIndex(m => m.IsArchived)
            .HasDatabaseName("IX_AgentMemory_IsArchived");

        // Reindex scan (StreamAsync with IndexPending = true).
        builder.HasIndex(m => m.IndexPending)
            .HasDatabaseName("IX_AgentMemory_IndexPending");

        // Row-value keyset paging in StreamAsync: ORDER BY / WHERE on ("CreatedAt", "Id").
        builder.HasIndex(m => new { m.CreatedAt, m.Id })
            .HasDatabaseName("IX_AgentMemory_CreatedAt_Id");
    }
}
