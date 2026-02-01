using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the TaskExecution entity.
/// </summary>
internal sealed class TaskExecutionConfiguration : IEntityTypeConfiguration<TaskExecution>
{
    public void Configure(EntityTypeBuilder<TaskExecution> entity)
    {
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Prompt)
            .IsRequired()
            .HasMaxLength(8000);

        entity.Property(e => e.LlmResponse)
            .IsRequired()
            .HasMaxLength(50000);

        entity.Property(e => e.Error)
            .HasMaxLength(2000);

        entity.HasIndex(e => e.TaskId)
            .HasDatabaseName("IX_TaskExecution_TaskId");

        entity.HasIndex(e => e.SessionId)
            .HasDatabaseName("IX_TaskExecution_SessionId");

        entity.HasIndex(e => e.ExecutedAt)
            .HasDatabaseName("IX_TaskExecution_ExecutedAt");
    }
}
