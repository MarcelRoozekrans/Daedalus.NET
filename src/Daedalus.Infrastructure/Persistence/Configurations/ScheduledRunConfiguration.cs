using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the <see cref="ScheduledRun"/> aggregate (table <c>ScheduledRuns</c>): one
///     configured recurring autonomous agent run, its cron schedule, delivery target, and execution identity.
/// </summary>
internal sealed class ScheduledRunConfiguration : IEntityTypeConfiguration<ScheduledRun>
{
    public void Configure(EntityTypeBuilder<ScheduledRun> builder)
    {
        builder.ToTable("ScheduledRuns");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Name).IsRequired().HasMaxLength(ScheduledRun.MaxNameLength);
        builder.Property(r => r.Cron).IsRequired().HasMaxLength(ScheduledRun.MaxCronLength);
        builder.Property(r => r.Trigger).IsRequired().HasMaxLength(ScheduledRun.MaxTriggerLength);
        builder.Property(r => r.ChannelId).IsRequired().HasMaxLength(ScheduledRun.MaxChannelIdLength);
        builder.Property(r => r.ConversationId).IsRequired().HasMaxLength(ScheduledRun.MaxConversationIdLength);
        builder.Property(r => r.PrincipalId).IsRequired().HasMaxLength(ScheduledRun.MaxPrincipalIdLength);

        // Nullable: only a RepoDigest schedule needs one, enforced by RepoDigestRepositoryValidator at boot,
        // not by this mapping.
        builder.Property(r => r.Repository).HasMaxLength(ScheduledRun.MaxRepositoryLength);
        builder.Property(r => r.Origin).IsRequired().HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.NextRunAt).IsRequired();
        builder.Property(r => r.LastRunAt);
        builder.Property(r => r.Enabled).IsRequired();
        builder.Property(r => r.MissedOccurrences).IsRequired();

        // Roles is a small fixed list of short tokens; a delimited column avoids a join table for data that is
        // always read whole and never queried by element.
        builder.Property(r => r.Roles)
            .IsRequired()
            .HasMaxLength(512)
            .HasConversion(
                v => string.Join(',', v),
                v => v.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
                (a, b) => a!.SequenceEqual(b!),
                v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode(StringComparison.Ordinal))),
                v => v.ToList()));

        // Name is the natural key reconciliation matches on; enforced by the database, not by convention,
        // so the reconciler's upsert can rely on it.
        builder.HasIndex(r => r.Name).IsUnique().HasDatabaseName("IX_ScheduledRun_Name");

        // The sweeper's hot query is "enabled and due"; this index serves it directly.
        builder.HasIndex(r => new { r.Enabled, r.NextRunAt }).HasDatabaseName("IX_ScheduledRun_Enabled_NextRunAt");

        // Optimistic concurrency for the claim in Task 6. xmin is Postgres's own system column — no extra
        // column, and it is updated by the database rather than by the application.
        builder.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin");
    }
}
