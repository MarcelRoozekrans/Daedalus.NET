using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the <see cref="ChannelConversation"/> aggregate (table
///     <c>ChannelConversations</c>): binds one external chat to the Thalos session currently serving it.
/// </summary>
internal sealed class ChannelConversationConfiguration : IEntityTypeConfiguration<ChannelConversation>
{
    public void Configure(EntityTypeBuilder<ChannelConversation> builder)
    {
        builder.ToTable("ChannelConversations");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.ChannelId)
            .IsRequired()
            .HasMaxLength(ChannelConversation.MaxChannelIdLength);

        builder.Property(c => c.ConversationId)
            .IsRequired()
            .HasMaxLength(ChannelConversation.MaxConversationIdLength);

        builder.Property(c => c.SessionId).IsRequired();
        builder.Property(c => c.AgentId).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.LastActivityAt).IsRequired();

        // The natural key: one row per external chat, regardless of which channel it came in on. Enforced by the
        // database (not merely by convention) so the store Task 3 builds on top can rely on it for upserts.
        builder.HasIndex(c => new { c.ChannelId, c.ConversationId })
            .IsUnique()
            .HasDatabaseName("IX_ChannelConversation_Channel_Conversation");

        // No index on SessionId: IConversationMap (GetAsync/BindAsync/UnbindAsync) is keyed on
        // (ChannelId, ConversationId) only. A reverse SessionId -> conversation lookup existed in plan A
        // (GetBySessionAsync) but was removed in 0.4.0 once the adapter's own signature was corrected to
        // address a conversation directly rather than by session; an index serving that removed access path
        // would be a ghost one layer down, costing write throughput on every bind for no reader.
    }
}
