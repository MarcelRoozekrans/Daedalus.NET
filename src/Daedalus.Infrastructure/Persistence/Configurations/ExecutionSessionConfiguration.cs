using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the ExecutionSession aggregate root.
/// </summary>
internal sealed class ExecutionSessionConfiguration : IEntityTypeConfiguration<ExecutionSession>
{
    public void Configure(EntityTypeBuilder<ExecutionSession> entity)
    {
        entity.HasKey(e => e.Id);

        entity.Property(e => e.WorkerName)
            .IsRequired()
            .HasMaxLength(256);

        entity.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_Session_IsActive");

        entity.HasIndex(e => e.LastHeartbeat)
            .HasDatabaseName("IX_Session_LastHeartbeat");

        // Configure concurrency token for optimistic locking
        entity.Property(e => e.RowVersion)
            .IsRowVersion();
    }
}
