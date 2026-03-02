using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the BrainstormMessage child entity.
/// </summary>
internal sealed class BrainstormMessageConfiguration : IEntityTypeConfiguration<BrainstormMessage>
{
    public void Configure(EntityTypeBuilder<BrainstormMessage> builder)
    {
        builder.ToTable("BrainstormMessages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.BrainstormSessionId).IsRequired();

        builder.Property(m => m.Role)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(m => m.Content)
            .IsRequired()
            .HasColumnType("text");

        builder.Property(m => m.Phase)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(m => m.CreatedAt).IsRequired();

        builder.HasIndex(m => m.BrainstormSessionId)
            .HasDatabaseName("IX_BrainstormMessage_SessionId");
    }
}
