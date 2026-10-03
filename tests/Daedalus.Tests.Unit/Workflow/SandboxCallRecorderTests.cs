using System.Text.Json;
using AwesomeAssertions.Execution;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Task B6: the sandbox call observer records <c>sandbox__test</c> and <c>sandbox__build</c> results as run records.
///     What it records is reported by the run's sandbox, which ran code the agent wrote, so it is never verified.
/// </summary>
public sealed class SandboxCallRecorderTests
{
    private static readonly Guid RunId = new Guid(0x2a3b4c5d, 0x6e7f, 0x4081, 0x92, 0xa3, 0xb4, 0xc5, 0xd6, 0xe7, 0xf8, 0x9) /* 2a3b4c5d-6e7f-4081-92a3-b4c5d6e7f809 */;
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private const string PassedResult =
        "exit: 0\nPassed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12\n--- output (last 4096 bytes) ---\nTAIL-OF-THE-OUTPUT";

    private readonly IWorkflowStore _store = Substitute.For<IWorkflowStore>();
    private readonly IWorkflowRunRecordStore _records = Substitute.For<IWorkflowRunRecordStore>();
    private readonly List<WorkflowRunRecord> _appended = [];

    public SandboxCallRecorderTests()
    {
        _store.FindAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<WorkflowRun?>(Run()));
        _records.AppendAsync(Arg.Do<WorkflowRunRecord>(_appended.Add), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);
    }

    private SandboxCallRecorder Recorder() => new(
        _store, RecordStoreScopes.For(_records), new FakeTimeProvider(Now), NullLogger<SandboxCallRecorder>.Instance);

    private static WorkflowRun Run() => new()
    {
        Id = RunId,
        Process = "manufacture",
        ProcessVersion = 7,
        CurrentNode = "implement",
        CurrentSeq = 4,
        Status = WorkflowStatus.Running,
        StartedBy = new RunPrincipal("u-starter", ["developer"]),
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
    };

    private static RunToolCall Call(string source, string tool, string text, TimeSpan? elapsed = null)
    {
        var caller = Substitute.For<ISecurityContext>();
        caller.Id.Returns("workflow:run/implement");
        return new RunToolCall(RunId, source, tool, caller, text, elapsed ?? TimeSpan.FromMilliseconds(1500));
    }

    private Task Record(RunToolCall call, CancellationToken ct = default) => Recorder().OnCompletedAsync(call, ct).AsTask();

    private static JsonElement Payload(WorkflowRunRecord record) => JsonDocument.Parse(record.PayloadJson).RootElement.Clone();

    /// <summary>
    ///     A test call is recorded at the run's current node and sequence, as the caller, with the exit and the summary
    ///     line and the elapsed time, and without the output tail. Red: omit the append, which fails the count; read the
    ///     exit from the second line, which fails the exit; keep the whole result as the summary, which fails the tail
    ///     assertion; write the node from a constant, which fails the node.
    /// </summary>
    [Fact]
    public async Task A_test_call_is_recorded_with_its_exit_and_summary()
    {
        await Record(Call("sandbox", "test", PassedResult));

        var record = _appended.Should().ContainSingle().Subject;
        var payload = Payload(record);
        using (new AssertionScope())
        {
            record.Kind.Should().Be(WorkflowRunRecord.TestResultKind);
            record.RunId.Should().Be(RunId);
            record.Node.Should().Be("implement");
            record.Seq.Should().Be(4);
            record.PrincipalId.Should().Be("workflow:run/implement");
            record.StartedById.Should().Be("u-starter");
            record.CreatedAt.Should().Be(Now.UtcDateTime);
            payload.GetProperty("tool").GetString().Should().Be("test");
            payload.GetProperty("exit").GetString().Should().Be("0");
            payload.GetProperty("summary").GetString().Should().Be("Passed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12");
            payload.GetProperty("elapsedMs").GetInt64().Should().Be(1500);
            record.PayloadJson.Should().NotContain("TAIL-OF-THE-OUTPUT", "no output tail is recorded");
        }
    }

    /// <summary>
    ///     Only <c>sandbox</c> calls to <c>test</c> and <c>build</c> are recorded; a workspace call or another sandbox
    ///     tool is not, and the run is not even read. Red: drop the source check, which fails the first case; drop the
    ///     tool check, which fails the second.
    /// </summary>
    [Theory]
    [InlineData("workspace", "test")]
    [InlineData("sandbox", "shell")]
    [InlineData("roslyn", "build")]
    public async Task A_workspace_call_is_not_recorded(string source, string tool)
    {
        await Record(Call(source, tool, PassedResult));

        _appended.Should().BeEmpty();
        await _store.DidNotReceive().FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A build call is recorded with its error count as the summary. Red: record only <c>test</c> calls.</summary>
    [Fact]
    public async Task A_build_call_is_recorded_with_its_error_count()
    {
        await Record(Call("sandbox", "build", "exit: 1\nerrors: 3\n--- output (last 10 bytes) ---\nboom"));

        var payload = Payload(_appended.Should().ContainSingle().Subject);
        payload.GetProperty("tool").GetString().Should().Be("build");
        payload.GetProperty("exit").GetString().Should().Be("1");
        payload.GetProperty("summary").GetString().Should().Be("errors: 3");
    }

    /// <summary>
    ///     A timeout is recorded as the exit text Thalos wrote, and a multi-line <c>Total tests:</c> block is kept as one
    ///     line, up to the output marker. Red: read only the line after the exit line, which fails the summary.
    /// </summary>
    [Fact]
    public async Task A_timed_out_call_and_a_multi_line_summary_are_read()
    {
        await Record(Call(
            "sandbox", "test", "exit: timed out after 00:05:00\nTotal tests: 9\n     Passed: 8\n     Failed: 1\n--- output (last 5 bytes) ---\nx"));

        var payload = Payload(_appended.Should().ContainSingle().Subject);
        payload.GetProperty("exit").GetString().Should().Be("timed out after 00:05:00");
        payload.GetProperty("summary").GetString().Should().Be("Total tests: 9 Passed: 8 Failed: 1");
    }

    /// <summary>
    ///     A copy or start failure, the single line <c>error: ...</c>, is recorded with exit <c>error</c> and that line
    ///     as its summary. Red: treat it as unreadable, which fails the exit and the summary.
    /// </summary>
    [Fact]
    public async Task An_error_result_is_recorded_with_exit_error_and_its_line()
    {
        await Record(Call("sandbox", "test", "error: could not copy the tree"));

        var payload = Payload(_appended.Should().ContainSingle().Subject);
        payload.GetProperty("exit").GetString().Should().Be("error");
        payload.GetProperty("summary").GetString().Should().Be("error: could not copy the tree");
    }

    /// <summary>
    ///     A result in neither shape is recorded as unknown with a fixed summary, never with its own text. Red: record
    ///     the first line whatever it is, which fails the summary.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("something else entirely, with ](javascript:alert) in it")]
    [InlineData("exit:\nPassed!")]
    public async Task A_result_in_no_known_shape_is_recorded_as_unknown(string text)
    {
        await Record(Call("sandbox", "test", text));

        var payload = Payload(_appended.Should().ContainSingle().Subject);
        payload.GetProperty("exit").GetString().Should().Be(SandboxCallRecorder.UnknownExit);
        payload.GetProperty("summary").GetString().Should().Be(SandboxCallRecorder.UnknownSummary);
    }

    /// <summary>
    ///     The summary is one line capped at 500 characters, and nothing after the output marker is read. Red: drop the
    ///     cap, which fails the length; drop the cut at the marker, which fails the tail assertion.
    /// </summary>
    [Fact]
    public async Task The_summary_is_capped_at_500_characters()
    {
        var text = "exit: 0\n" + new string('a', 3000) + "\n--- output (last 9 bytes) ---\nTAIL-OF-THE-OUTPUT";

        await Record(Call("sandbox", "test", text));

        var summary = Payload(_appended.Should().ContainSingle().Subject).GetProperty("summary").GetString()!;
        summary.Length.Should().Be(500);
        summary.Should().EndWith("…").And.NotContain("TAIL-OF-THE-OUTPUT");
    }

    /// <summary>
    ///     Text the sandbox produced is stored as one line with control characters removed, so a summary that carries a
    ///     newline, a link, a mention and a backtick reaches the store as a single line. The body renders it inert in a
    ///     code span, which <c>PullRequestBodyTests</c> pins. Red: store the line as it is, which fails the
    ///     control-character assertion and the value.
    /// </summary>
    [Fact]
    public async Task The_summary_is_stored_as_one_line_without_control_characters()
    {
        const string hostile = "Passed! [x](javascript:alert) @mallory #123 `tick`\u202E\u0007\u2028second\r\nthird";

        await Record(Call("sandbox", "test", "exit: 0\n" + hostile));

        var summary = Payload(_appended.Should().ContainSingle().Subject).GetProperty("summary").GetString()!;
        using (new AssertionScope())
        {
            summary.Should().NotContainAny("\n", "\r", "\u0007", "\u2028", "\u202E");
            summary.Should().Be("Passed! [x](javascript:alert) @mallory #123 `tick` second third");
        }
    }

    /// <summary>
    ///     The observer never throws into the call: a run that is gone, a record store that fails and a run store that
    ///     times out are each logged and dropped. Red: let any of them propagate.
    /// </summary>
    [Fact]
    public async Task A_failure_to_find_the_run_or_to_append_never_throws_into_the_call()
    {
        _store.FindAsync(RunId, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>((WorkflowRun?)null));
        await FluentActions.Awaiting(() => Record(Call("sandbox", "test", PassedResult))).Should().NotThrowAsync();

        _store.FindAsync(RunId, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(Run()));
        _records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask>(_ => throw new InvalidOperationException("database is down"));
        await FluentActions.Awaiting(() => Record(Call("sandbox", "test", PassedResult))).Should().NotThrowAsync();

        _store.FindAsync(RunId, Arg.Any<CancellationToken>()).Returns<ValueTask<WorkflowRun?>>(_ => throw new TimeoutException("store timed out"));
        await FluentActions.Awaiting(() => Record(Call("sandbox", "test", PassedResult))).Should().NotThrowAsync();
    }

    /// <summary>Only the call's own cancellation leaves the observer as an exception. Red: catch every exception.</summary>
    [Fact]
    public async Task The_calls_own_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _store.FindAsync(RunId, Arg.Any<CancellationToken>())
            .Returns<ValueTask<WorkflowRun?>>(call => throw new OperationCanceledException(call.Arg<CancellationToken>()));

        await FluentActions.Awaiting(() => Record(Call("sandbox", "test", PassedResult), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
