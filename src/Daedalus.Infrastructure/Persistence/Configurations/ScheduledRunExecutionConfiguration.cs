using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the <see cref="ScheduledRunExecution"/> aggregate (table
///     <c>ScheduledRunExecutions</c>): one row per firing of a <see cref="ScheduledRun"/>.
/// </summary>
internal sealed class ScheduledRunExecutionConfiguration : IEntityTypeConfiguration<ScheduledRunExecution>
{
    public void Configure(EntityTypeBuilder<ScheduledRunExecution> builder)
    {
        builder.ToTable("ScheduledRunExecutions");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.ScheduleId).IsRequired();
        builder.Property(e => e.OccurrenceAt).IsRequired();
        builder.Property(e => e.Step).IsRequired().HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.Findings);
        builder.Property(e => e.Digest);
        builder.Property(e => e.ChannelId).IsRequired().HasMaxLength(ScheduledRunExecution.MaxChannelIdLength);
        builder.Property(e => e.ConversationId).IsRequired().HasMaxLength(ScheduledRunExecution.MaxConversationIdLength);
        builder.Property(e => e.PrincipalId).IsRequired().HasMaxLength(ScheduledRunExecution.MaxPrincipalIdLength);
        builder.Property(e => e.Attempts).IsRequired();
        builder.Property(e => e.LastError);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        // Same delimited-column treatment as ScheduledRun.Roles, and the same ValueComparer requirement:
        // without it EF cannot detect changes to a converted collection and the column silently never updates.
        builder.Property(e => e.Roles)
            .IsRequired()
            .HasMaxLength(512)
            .HasConversion(
                v => string.Join(',', v),
                v => v.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
                (a, b) => a!.SequenceEqual(b!),
                v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode(StringComparison.Ordinal))),
                v => v.ToList()));

        // THE correctness constraint of this phase. The saga's correlation key, expressed in the schema:
        // one execution per schedule-and-occurrence, enforced by the database rather than by any handler's
        // diligence. Task 13's INSERT ... ON CONFLICT names this index.
        builder.HasIndex(e => new { e.ScheduleId, e.OccurrenceAt })
            .IsUnique()
            .HasDatabaseName("IX_ScheduledRunExecution_Schedule_Occurrence");

        // Optimistic concurrency between two pollers racing one step. xmin is Postgres's own system column:
        // no extra column, and the database maintains it rather than the application.
        builder.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin");
    }
}
