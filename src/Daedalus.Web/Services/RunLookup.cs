using Daedalus.Application.DTOs.WorkflowRuns;

namespace Daedalus.Web.Services;

/// <summary>
///     What <c>GET /api/workflow-runs/{id}</c> answered when it was not an error: the run, no such run, or a refusal
///     to show it to this user. Each is a page state of its own, so none of them is a failed Result.
/// </summary>
/// <param name="Run">The run, or <see langword="null"/> when there is none to show.</param>
/// <param name="Forbidden">Whether the server refused the run to this user, a 403.</param>
public sealed record RunLookup(WorkflowRunView? Run, bool Forbidden)
{
    /// <summary>The server has no such run: a 404.</summary>
    public static RunLookup NotFound { get; } = new(null, Forbidden: false);

    /// <summary>The server refused the run to this user: a 403.</summary>
    public static RunLookup Denied { get; } = new(null, Forbidden: true);

    /// <summary>The run arrived.</summary>
    public static RunLookup Found(WorkflowRunView run) => new(run, Forbidden: false);
}
