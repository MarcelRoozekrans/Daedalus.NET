using Daedalus.Api.Controllers;
using Thalos;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary><see cref="WorkflowRunStepProjection"/> turns the run's event log into the timeline, one step per event.</summary>
public sealed class WorkflowRunStepProjectionTests
{
    /// <summary>
    ///     Each event becomes one step in seq order with its nodes, status, outcome, error, actor and time, and an
    ///     event with usage carries its tokens while one without carries none.
    ///     Red: sort steps by seq descending. Red: map ToNode from FromNode. Red: drop Actor.
    ///     Red: map InputTokens from OutputTokens. Red: map Error to null. Red: drop OrderBy.
    /// </summary>
    [Fact]
    public void Each_event_becomes_one_step_in_order()
    {
        var at = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
        IReadOnlyList<WorkflowRunEvent> events =
        [
            Event(5, "Retried", "publish", "publish", "Running", null, null, null, at.AddMinutes(9), "approver-1"),
            Event(0, "Entered", null, "implement", "Running", null, null, null, at, null),
            Event(7, "Failed", "publish", "publish", "Failed", null, "push refused", null, at.AddMinutes(12), null),
            Event(1, "Completed", "implement", "review", "Running", "done", null, Usage(1200, 80, 900), at.AddMinutes(4), null),
        ];

        var steps = WorkflowRunStepProjection.Project(events);

        steps.Select(s => s.Seq).Should().Equal(0, 1, 5, 7);
        steps[1].Should().BeEquivalentTo(new
        {
            Kind = "Completed",
            FromNode = "implement",
            ToNode = "review",
            Status = "Running",
            Outcome = "done",
            InputTokens = 1200,
            OutputTokens = 80,
            CacheReadTokens = 900,
            At = at.AddMinutes(4),
        });
        steps[0].InputTokens.Should().BeNull();
        steps[2].Actor.Should().Be("approver-1");
        steps[3].Error.Should().Be("push refused");
    }

    private static WorkflowRunEvent Event(
        long seq, string kind, string? from, string to, string status, string? outcome, string? error, TurnUsage? usage,
        DateTimeOffset at, string? actor) =>
        new(seq, kind, from, to, status, outcome, error, usage, at, actor is null ? null : new RunPrincipal(actor, []));

    private static TurnUsage Usage(int input, int output, int cacheRead) =>
        new(input, output, "m") { CacheReadTokens = cacheRead };
}
