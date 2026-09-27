using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     <see cref="WorkflowStrandedRunSweepService.SweepOnceAsync"/>: only a cancellation of the stopping token
///     ends the sweep loop. Any other failure, including a cancellation the store raised on its own, is logged
///     and left for the next tick.
/// </summary>
public sealed class WorkflowStrandedRunSweepServiceTests
{
    [Fact]
    public async Task A_cancellation_the_store_raises_on_its_own_is_a_failed_sweep_not_a_stop()
    {
        var store = Substitute.For<IWorkflowStore>();
        store.FindStrandedAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<IReadOnlyList<WorkflowRun>>>(_ => throw new OperationCanceledException("command timeout"));
        var service = CreateService(store);

        var thrown = await Record.ExceptionAsync(() => service.SweepOnceAsync(CancellationToken.None));

        thrown.Should().BeNull("the host is not stopping, so the loop must survive to sweep again next tick");
    }

    [Fact]
    public async Task A_stop_during_a_sweep_propagates()
    {
        // The host has been asked to stop, and the store's call observes it.
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var store = Substitute.For<IWorkflowStore>();
        store.FindStrandedAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<IReadOnlyList<WorkflowRun>>>(_ => throw new OperationCanceledException(stopping.Token));
        var service = CreateService(store);

        var thrown = await Record.ExceptionAsync(() => service.SweepOnceAsync(stopping.Token));

        thrown.Should().BeAssignableTo<OperationCanceledException>("a stop ends the loop promptly instead of being logged");
    }

    [Fact]
    public async Task The_sweep_asks_the_store_for_runs_older_than_the_threshold_it_was_given()
    {
        var store = Substitute.For<IWorkflowStore>();
        store.FindStrandedAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<WorkflowRun>>([]));
        var service = CreateService(store, TimeSpan.FromMinutes(42));

        await service.SweepOnceAsync(CancellationToken.None);

        await store.Received(1).FindStrandedAsync(TimeSpan.FromMinutes(42), Arg.Any<CancellationToken>());
    }

    private static WorkflowStrandedRunSweepService CreateService(IWorkflowStore store, TimeSpan? strandedAfter = null) =>
        new(new WorkflowRunReconciler(store), strandedAfter ?? TimeSpan.FromMinutes(35), NullLogger<WorkflowStrandedRunSweepService>.Instance);
}
