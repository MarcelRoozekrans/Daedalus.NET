using Daedalus.Agents.Workflow;
using Daedalus.Tests.Unit.Controllers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     The compensating cancel of a run that started but could not be attached: under a token of its own with a timeout,
///     never the caller's, retried once on a lost concurrency race and never on anything else.
/// </summary>
public sealed class WorkflowRunCancellerTests
{
    private const string Reason = "the reason";

    private readonly IWorkflowStore _store = Substitute.For<IWorkflowStore>();
    private readonly FakeTimeProvider _clock = new();
    private readonly TaskManufactureServiceTests.CapturingLogger<WorkflowRunCanceller> _logger = new();
    private readonly Guid _runId = Guid.NewGuid();

    private WorkflowRunCanceller Canceller() => new(TaskManufactureServiceTests.Gateway(_store), _clock, _logger);

    /// <summary>
    ///     A client that gave up during a slow start must not leave its run running.
    ///     Red: pass the caller's token to the store; the cancelled token makes the store throw and the run is not cancelled.
    /// </summary>
    [Fact]
    public async Task A_cancelled_caller_token_still_cancels_the_run()
    {
        _store.CancelAsync(Guid.Empty, default!, default).ReturnsForAnyArgs(call =>
        {
            call.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        });
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        (await Canceller().CancelAsync(_runId, Reason, caller.Token)).Should().BeTrue();

        await _store.Received(1).CancelAsync(_runId, Reason, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     An attempt that hangs is given up after <see cref="WorkflowRunCanceller.CancelTimeout"/> on the host clock.
    ///     Red: run the attempt under <see cref="CancellationToken.None"/>; the call never completes and the guard times out.
    /// </summary>
    [Fact]
    public async Task A_hanging_cancel_is_given_up_after_the_timeout()
    {
        _store.CancelAsync(Guid.Empty, default!, default).ReturnsForAnyArgs(call =>
            new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(2))));

        var cancelling = Canceller().CancelAsync(_runId, Reason, CancellationToken.None).AsTask();
        _clock.Advance(WorkflowRunCanceller.CancelTimeout);

        (await cancelling.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
    }

    /// <summary>
    ///     Another write between the store's read and its update loses the first attempt; the second reads the run fresh.
    ///     Red: remove the retry; the cancel reports false after one call.
    /// </summary>
    [Fact]
    public async Task A_lost_concurrency_race_is_retried_once_and_the_retry_cancels()
    {
        var calls = 0;
        _store.CancelAsync(Guid.Empty, default!, default).ReturnsForAnyArgs(_ =>
            ++calls == 1 ? throw new WorkflowConcurrencyException("another writer changed the run") : ValueTask.CompletedTask);

        (await Canceller().CancelAsync(_runId, Reason, CancellationToken.None)).Should().BeTrue();

        await _store.Received(2).CancelAsync(_runId, Reason, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Two lost races give up after the retry, logged.
    ///     Red: retry twice; the store receives three calls.
    /// </summary>
    [Fact]
    public async Task Two_lost_races_give_up_after_one_retry()
    {
        _store.CancelAsync(Guid.Empty, default!, default).ReturnsForAnyArgs<ValueTask>(_ => throw new WorkflowConcurrencyException("again"));

        (await Canceller().CancelAsync(_runId, Reason, CancellationToken.None)).Should().BeFalse();

        await _store.Received(2).CancelAsync(_runId, Reason, Arg.Any<CancellationToken>());
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Which.Exception.Should().BeOfType<WorkflowConcurrencyException>();
    }

    /// <summary>
    ///     Any other failure is not retried: it is logged and reported.
    ///     Red: retry every exception; the store receives two calls.
    /// </summary>
    [Fact]
    public async Task Another_failure_is_not_retried()
    {
        _store.CancelAsync(Guid.Empty, default!, default).ReturnsForAnyArgs<ValueTask>(_ => throw new InvalidOperationException("store down"));

        (await Canceller().CancelAsync(_runId, Reason, CancellationToken.None)).Should().BeFalse();

        await _store.Received(1).CancelAsync(_runId, Reason, Arg.Any<CancellationToken>());
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Which.Exception.Should().BeOfType<InvalidOperationException>();
    }
}
