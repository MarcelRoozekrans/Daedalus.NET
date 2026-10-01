using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for <see cref="WorkflowRunRecord"/> (table <c>WorkflowRunRecords</c>). Insert-only:
///     nothing in this codebase updates or deletes a row here, so no concurrency token is configured. There is no
///     foreign key on <see cref="WorkflowRunRecord.RunId"/>: the Thalos <c>workflow_run</c> table it names belongs
///     to another schema with its own migrations.
/// </summary>
internal sealed class WorkflowRunRecordConfiguration : IEntityTypeConfiguration<WorkflowRunRecord>
{
    public void Configure(EntityTypeBuilder<WorkflowRunRecord> builder)
    {
        builder.ToTable("WorkflowRunRecords");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.RunId).IsRequired();
        builder.Property(r => r.Seq).IsRequired();

        builder.Property(r => r.Node)
            .IsRequired()
            .HasMaxLength(WorkflowRunRecord.MaxNodeLength);

        builder.Property(r => r.Kind)
            .IsRequired()
            .HasMaxLength(WorkflowRunRecord.MaxKindLength);

        builder.Property(r => r.PrincipalId)
            .IsRequired()
            .HasMaxLength(WorkflowRunRecord.MaxPrincipalIdLength);

        builder.Property(r => r.StartedById)
            .HasMaxLength(WorkflowRunRecord.MaxPrincipalIdLength);

        builder.Property(r => r.PayloadJson)
            .IsRequired()
            .HasColumnType("jsonb");

        builder.Property(r => r.CreatedAt).IsRequired();

        // Serves both readers: a run's whole record, and one kind of it, each in Seq order.
        builder.HasIndex(r => new { r.RunId, r.Kind, r.Seq })
            .HasDatabaseName("IX_WorkflowRunRecords_RunId_Kind_Seq");
    }
}
