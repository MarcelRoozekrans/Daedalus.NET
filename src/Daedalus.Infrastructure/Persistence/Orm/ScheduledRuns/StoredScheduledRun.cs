using ScheduledRun = Daedalus.Domain.Entities.ScheduledRun;
using ScheduleOrigin = Daedalus.Domain.Entities.ScheduleOrigin;

namespace Daedalus.Infrastructure.Persistence.Orm.ScheduledRuns;

/// <summary>
///     The columns of <c>"ScheduledRuns"</c> as they are stored, so a LINQ chain can name them; nothing constructs it.
/// </summary>
/// <remarks>
///     Ruling R113: the table is mapped through this type, not <see cref="ScheduledRun"/>, because
///     <see cref="ScheduledRun.Roles"/> is a list while its column is one comma-joined <c>character varying</c>, and the
///     ORM converts no values, so a map of the domain type would read and bind the column as <c>text[]</c>. Every other
///     column has its domain property's name and type; <see cref="ScheduledRunRow"/> splits the roles and
///     <see cref="ScheduledRunSql"/> joins them, as EF Core's value converter did.
/// </remarks>
internal sealed class StoredScheduledRun
{
    /// <summary>The schedule id, the primary key.</summary>
    public Guid Id { get; init; }

    /// <summary>The unique schedule name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The cron expression.</summary>
    public string Cron { get; init; } = "";

    /// <summary>What runs when the schedule fires.</summary>
    public string Trigger { get; init; } = "";

    /// <summary>The channel adapter that receives the output.</summary>
    public string ChannelId { get; init; } = "";

    /// <summary>The conversation that receives the output.</summary>
    public string ConversationId { get; init; } = "";

    /// <summary>The identity the run executes as.</summary>
    public string PrincipalId { get; init; } = "";

    /// <summary>The roles, comma-joined.</summary>
    public string Roles { get; init; } = "";

    /// <summary>Where the definition came from, stored by its member name.</summary>
    public ScheduleOrigin Origin { get; init; }

    /// <summary>When the schedule next fires, in UTC.</summary>
    public DateTime NextRunAt { get; init; }

    /// <summary>When the schedule last fired, in UTC, or null.</summary>
    public DateTime? LastRunAt { get; init; }

    /// <summary>Whether the schedule is active.</summary>
    public bool Enabled { get; init; }

    /// <summary>The cumulative count of missed occurrences.</summary>
    public int MissedOccurrences { get; init; }

    /// <summary>The repository the workflow sweeps, or null.</summary>
    public string? Repository { get; init; }
}
