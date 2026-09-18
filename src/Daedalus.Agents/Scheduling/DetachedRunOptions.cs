namespace Daedalus.Agents.Scheduling;

/// <summary>
///     Binds the <c>DetachedRuns</c> configuration section: the identity and budget a scheduled run executes
///     under when nothing routed it through a live human turn. See <see cref="DetachedPrincipal"/> for why the
///     identity is configured rather than borrowed, and <see cref="SubagentRunExecutor"/> for how the budget is
///     applied.
/// </summary>
public sealed class DetachedRunOptions
{
    /// <summary>The <see cref="ZeroAlloc.Authorization.ISecurityContext.Id"/> a detached run reports as its caller.</summary>
    public required string PrincipalId { get; init; }

    /// <summary>The roles a detached run's <see cref="DetachedPrincipal"/> carries.</summary>
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>
    ///     The token ceiling checked after the turn completes. A budget settles an overspend into a reported
    ///     failure; it cannot stop a turn mid-flight because the turn is buffered.
    /// </summary>
    public required int MaxTotalTokens { get; init; }

    /// <summary>The wall-clock deadline that stops a run from waiting on a stalled provider forever.</summary>
    public required int DeadlineSeconds { get; init; }
}
