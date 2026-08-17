using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for AgentMessage (one serialized ChatMessage of a Thalos session).
/// </summary>
internal sealed class AgentMessageConfiguration : IEntityTypeConfiguration<AgentMessage>
{
    public void Configure(EntityTypeBuilder<AgentMessage> builder)
    {
        builder.ToTable("AgentMessages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.SessionId).IsRequired();
        builder.Property(m => m.Sequence).IsRequired();

        builder.Property(m => m.Role)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(m => m.ContentJson)
            .IsRequired()
            .HasColumnType("jsonb");

        builder.Property(m => m.ModelId)
            .HasMaxLength(128);

        builder.Property(m => m.CreatedAt).IsRequired();

        builder.HasOne<AgentSession>()
            .WithMany()
            .HasForeignKey(m => m.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => new { m.SessionId, m.Sequence })
            .IsUnique()
            .HasDatabaseName("IX_AgentMessage_Session_Sequence");
    }
}
