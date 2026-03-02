using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the BrainstormSession aggregate root.
/// </summary>
internal sealed class BrainstormSessionConfiguration : IEntityTypeConfiguration<BrainstormSession>
{
    public void Configure(EntityTypeBuilder<BrainstormSession> builder)
    {
        builder.ToTable("BrainstormSessions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.ProjectId).IsRequired();

        builder.Property(s => s.Phase)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(s => s.DesignDocument)
            .HasColumnType("text");

        builder.Property(s => s.ImplementationPlan)
            .HasColumnType("text");

        builder.Property(s => s.PhaseCompleteSignaled)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.CompletedAt);

        builder.Property(s => s.RowVersion)
            .IsRowVersion();

        builder.HasMany(s => s.Messages)
            .WithOne()
            .HasForeignKey(m => m.BrainstormSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.ProjectId)
            .HasDatabaseName("IX_BrainstormSession_ProjectId");

        builder.HasIndex(s => s.Phase)
            .HasDatabaseName("IX_BrainstormSession_Phase");
    }
}
