using RunStep = Daedalus.Domain.Entities.RunStep;
using ScheduledRunExecution = Daedalus.Domain.Entities.ScheduledRunExecution;

namespace Daedalus.Infrastructure.Persistence.Orm.ScheduledRunExecutions;

/// <summary>
///     The columns of <c>"ScheduledRunExecutions"</c> as they are stored, so a LINQ chain can name them; nothing
///     constructs it.
/// </summary>
/// <remarks>
///     Ruling R119: the table is mapped through this type, not <see cref="ScheduledRunExecution"/>, for the reason
///     ruling R113 gives for <c>"ScheduledRuns"</c>: <see cref="ScheduledRunExecution.Roles"/> is a list while its
///     column is one comma-joined <c>character varying</c>, and the ORM converts no values. Every other column has its
///     domain property's name and type; <see cref="ScheduledRunExecutionRow"/> splits the roles and
///     <see cref="ScheduledRunExecutionSql"/> joins them, as EF Core's value converter did.
/// </remarks>
internal sealed class StoredScheduledRunExecution
{
    /// <summary>The execution id, the primary key.</summary>
    public Guid Id { get; init; }

    /// <summary>The schedule that fired.</summary>
    public Guid ScheduleId { get; init; }

    /// <summary>The instant the occurrence was due, in UTC.</summary>
    public DateTime OccurrenceAt { get; init; }

    /// <summary>The step that should happen next, stored by its member name.</summary>
    public RunStep Step { get; init; }

    /// <summary>The step the run failed at, stored by its member name, or null.</summary>
    public RunStep? FailedAtStep { get; init; }

    /// <summary>The scout stage's output, or null.</summary>
    public string? Findings { get; init; }

    /// <summary>The writer stage's output, or null.</summary>
    public string? Digest { get; init; }

    /// <summary>The channel adapter that receives the output.</summary>
    public string ChannelId { get; init; } = "";

    /// <summary>The conversation that receives the output.</summary>
    public string ConversationId { get; init; } = "";

    /// <summary>The identity the run executes as.</summary>
    public string PrincipalId { get; init; } = "";

    /// <summary>The roles, comma-joined.</summary>
    public string Roles { get; init; } = "";

    /// <summary>How many times the execution has failed.</summary>
    public int Attempts { get; init; }

    /// <summary>The most recent failure's message, or null.</summary>
    public string? LastError { get; init; }

    /// <summary>When the execution was created, in UTC.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>When the execution was last updated, in UTC.</summary>
    public DateTime UpdatedAt { get; init; }
}
