using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the Task aggregate root.
/// </summary>
internal sealed class TaskConfiguration : IEntityTypeConfiguration<Task>
{
    public void Configure(EntityTypeBuilder<Task> entity)
    {
        entity.HasKey(e => e.Id);

        entity.Property(e => e.TaskId)
            .IsRequired()
            .HasMaxLength(50);

        entity.Property(e => e.Title)
            .IsRequired()
            .HasMaxLength(500);

        entity.Property(e => e.Description)
            .IsRequired()
            .HasMaxLength(2000);

        entity.Property(e => e.Prompt)
            .IsRequired()
            .HasMaxLength(8000);

        entity.Property(e => e.CompletionPromise)
            .IsRequired()
            .HasMaxLength(1000);

        entity.Property(e => e.Phase)
            .IsRequired()
            .HasMaxLength(100);

        entity.Property(e => e.Status)
            .HasConversion<int>()
            .IsRequired();

        entity.Property(e => e.Priority)
            .HasConversion<int>()
            .IsRequired();

        entity.Property(e => e.EstimatedComplexity)
            .HasConversion<int>()
            .IsRequired();

        entity.Property(e => e.Result)
            .HasMaxLength(50000);

        entity.Property(e => e.Learnings)
            .HasMaxLength(10000);

        // Configure concurrency token for optimistic locking
        entity.Property(e => e.RowVersion)
            .IsRowVersion();

        // Configure list properties as PostgreSQL arrays
        entity.Property("_dependencies")
            .HasColumnName("Dependencies")
            .HasColumnType("text[]");

        entity.Property("_filesToModify")
            .HasColumnName("FilesToModify")
            .HasColumnType("text[]");

        entity.HasMany(e => e.Executions)
            .WithOne()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.Status)
            .HasDatabaseName("IX_Task_Status");

        entity.HasIndex(e => e.CurrentSessionId)
            .HasDatabaseName("IX_Task_SessionId");

        entity.HasIndex(e => e.CreatedAt)
            .HasDatabaseName("IX_Task_CreatedAt");

        entity.HasIndex(e => e.ProjectId)
            .HasDatabaseName("IX_Tasks_ProjectId");
    }
}
