using Daedalus.Application.DTOs.WorkflowRuns;
using Thalos.Workflow;

namespace Daedalus.Api.Controllers;

/// <summary>The run's event log as the console's timeline: one step per event, in seq order.</summary>
internal static class WorkflowRunStepProjection
{
    /// <summary>Projects <paramref name="events"/>, which the store returns in ascending seq order.</summary>
    public static IReadOnlyList<WorkflowRunStepView> Project(IReadOnlyList<WorkflowRunEvent> events) =>
    [
        .. events.OrderBy(e => e.Seq).Select(e => new WorkflowRunStepView(
            e.Seq, e.Kind, e.FromNode, e.ToNode, e.Status, e.Outcome, e.Error,
            e.Usage?.InputTokens, e.Usage?.OutputTokens, e.Usage?.CacheReadTokens, e.CreatedAt, e.Actor?.Id)),
    ];
}
