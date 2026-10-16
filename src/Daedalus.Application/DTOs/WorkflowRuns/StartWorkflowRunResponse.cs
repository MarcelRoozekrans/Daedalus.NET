namespace Daedalus.Application.DTOs.WorkflowRuns;

/// <summary>
///     Response body for starting a run: <c>WorkflowRunsController.Start</c> and <c>TasksController.Manufacture</c>. The
///     Web console reads the same type, so it keeps no copy of its own.
/// </summary>
/// <param name="RunId">The id of the run that was just started.</param>
public sealed record StartWorkflowRunResponse(Guid RunId);
