using System.Collections.Concurrent;
using Daedalus.Api.Hubs;
using Daedalus.Application.DTOs.WorkflowRuns;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Hubs;

/// <summary>The broadcaster turns each committed change into one RunChanged message to every client, off the store's path.</summary>
public sealed class WorkflowRunChangeBroadcasterTests
{
    /// <summary>
    ///     A change becomes exactly one message with the run's id, status, node and signal.
    ///     Red: send change.Kind as the status. Red: send to Clients.Group instead of All.
    /// </summary>
    [Fact]
    public async Task A_change_is_sent_to_every_client()
    {
        var hub = Substitute.For<IHubContext<WorkflowRunsHub>>();
        var sent = new TaskCompletionSource<object?[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Clients.All.SendCoreAsync(
                WorkflowRunsHub.RunChangedMethod, Arg.Do<object?[]>(a => sent.TrySetResult(a)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        using var broadcaster = new WorkflowRunChangeBroadcaster(hub, NullLogger<WorkflowRunChangeBroadcaster>.Instance);
        await broadcaster.StartAsync(CancellationToken.None);
        var runId = Guid.NewGuid();

        await broadcaster.OnChangedAsync(
            new WorkflowRunChange(runId, WorkflowRunChangeKind.Resumed, WorkflowStatus.Running, "publish", null), CancellationToken.None);

        var args = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        args.Should().ContainSingle().Which.Should().Be(new WorkflowRunChanged(runId, "Running", "publish", null));
        await broadcaster.StopAsync(CancellationToken.None);
    }

    /// <summary>
    ///     The observer returns before any send completes, so a slow client never holds the store.
    ///     Red: await the send inside OnChangedAsync.
    /// </summary>
    [Fact]
    public async Task The_observer_never_waits_for_a_send()
    {
        var hub = Substitute.For<IHubContext<WorkflowRunsHub>>();
        hub.Clients.All.SendCoreAsync(default!, default!, default).ReturnsForAnyArgs(new TaskCompletionSource().Task);
        using var broadcaster = new WorkflowRunChangeBroadcaster(hub, NullLogger<WorkflowRunChangeBroadcaster>.Instance);
        await broadcaster.StartAsync(CancellationToken.None);

        var observed = broadcaster.OnChangedAsync(
            new WorkflowRunChange(Guid.NewGuid(), WorkflowRunChangeKind.Started, WorkflowStatus.Running, "implement", null),
            CancellationToken.None);

        observed.IsCompletedSuccessfully.Should().BeTrue();
        await broadcaster.StopAsync(CancellationToken.None);
    }

    /// <summary>
    ///     A failed send is logged by the exception's type, never its message, and the loop goes on to the next change.
    ///     Red: log exception.Message instead of the type name: the type is missing from the line.
    ///     Red: log the type name followed by exception.Message: the sentinel reaches the line.
    ///     Red: let the exception end the loop: the second change is never sent.
    /// </summary>
    [Fact]
    public async Task A_failed_send_is_logged_by_type_and_the_next_change_is_still_sent()
    {
        var hub = Substitute.For<IHubContext<WorkflowRunsHub>>();
        var failing = Guid.NewGuid();
        var next = Guid.NewGuid();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Clients.All.SendCoreAsync(default!, default!, default).ReturnsForAnyArgs(call =>
        {
            var args = call.ArgAt<object?[]>(1);
            if (args is [WorkflowRunChanged message] && message.Id == failing)
            {
                return Task.FromException(new InvalidOperationException("zqx-send-secret"));
            }

            delivered.TrySetResult();
            return Task.CompletedTask;
        });
        var logger = new CapturingLogger();
        using var broadcaster = new WorkflowRunChangeBroadcaster(hub, logger);
        await broadcaster.StartAsync(CancellationToken.None);

        await broadcaster.OnChangedAsync(
            new WorkflowRunChange(failing, WorkflowRunChangeKind.Started, WorkflowStatus.Running, "implement", null), CancellationToken.None);
        await broadcaster.OnChangedAsync(
            new WorkflowRunChange(next, WorkflowRunChangeKind.Started, WorkflowStatus.Running, "implement", null), CancellationToken.None);

        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await broadcaster.StopAsync(CancellationToken.None);
        var line = logger.Lines.Should().ContainSingle().Subject;
        line.Should().Contain(typeof(InvalidOperationException).FullName).And.Contain(failing.ToString());
        line.Should().NotContain("zqx-send-secret");
    }

    private sealed class CapturingLogger : ILogger<WorkflowRunChangeBroadcaster>
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue($"{logLevel} {formatter(state, exception)} {exception}");
    }
}
