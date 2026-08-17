using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the AgentSession aggregate root (Thalos session header).
/// </summary>
internal sealed class AgentSessionConfiguration : IEntityTypeConfiguration<AgentSession>
{
    public void Configure(EntityTypeBuilder<AgentSession> builder)
    {
        builder.ToTable("AgentSessions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.AgentId).IsRequired();

        builder.Property(s => s.OwnerId)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(s => s.State)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.LastActivityAt).IsRequired();
        builder.Property(s => s.TurnCount).IsRequired();
        builder.Property(s => s.TotalInputTokens).IsRequired();
        builder.Property(s => s.TotalOutputTokens).IsRequired();

        builder.Property(s => s.RowVersion)
            .IsRowVersion();

        builder.HasIndex(s => new { s.OwnerId, s.CreatedAt })
            .HasDatabaseName("IX_AgentSession_Owner_CreatedAt");

        builder.HasIndex(s => s.AgentId)
            .HasDatabaseName("IX_AgentSession_AgentId");
    }
}
