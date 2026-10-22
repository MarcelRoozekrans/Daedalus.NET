using System.Text.Json;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.13 task 3: <see cref="ReviewHandoffWorkflowStore"/> keeps every implement visit's <c>review_response</c> as a
///     <c>review-response</c> record at that visit's seq, because the run variable is overwritten by each visit.
/// </summary>
public sealed class ReviewResponseRecordingTests
{
    private const long ImplementSeq = 7;

    private const string Response = """["addressed: src/A.cs:12 - added the missing case check"]""";

    private static WorkflowRun RunAt(string node, long currentSeq = ImplementSeq, WorkflowStatus status = WorkflowStatus.Running) => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 6,
        CurrentNode = node,
        CurrentSeq = currentSeq,
        Status = status,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { [node] = 2 },
        Variables = new Dictionary<string, object?>(StringComparer.Ordinal),
        StartedBy = new RunPrincipal("starter-1", ["developer"]),
    };

    /// <summary>The value as the engine hands it over: a JSON element parsed out of the outcome tool's arguments.</summary>
    private static JsonElement ReportedResponse() => JsonDocument.Parse(Response).RootElement.Clone();

    private static (RecordingWorkflowStore Inner, ReviewHandoffWorkflowStore Store, List<WorkflowRunRecord> Appended) Build(
        WorkflowRun run, Exception? appendFails = null)
    {
        var appended = new List<WorkflowRunRecord>();
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (appendFails is not null)
            {
                throw appendFails;
            }

            appended.Add(call.Arg<WorkflowRunRecord>());
            return ValueTask.CompletedTask;
        });
        var inner = new RecordingWorkflowStore(run);
        var store = new ReviewHandoffWorkflowStore(
            inner, HostWrittenKeyTests.Definitions(), TestNodeUsage.Recorder(), TestNodeUsage.Responses(records));
        return (inner, store, appended);
    }

    private static ValueTask CompleteAsync(ReviewHandoffWorkflowStore store, WorkflowRun run, Dictionary<string, object?> reported) =>
        store.CompleteNodeAsync(
            run.Id, ImplementSeq, new WorkflowTransition("review", WorkflowStatus.Running, awaitingSignal: null, WorkflowEventKind.Branched),
            new NodeResult("changed", reported), CancellationToken.None);

    /// <summary>
    ///     An implement visit that reports a response leaves one review-response record at its own seq, holding the value as
    ///     written, and still completes.
    ///     Red: drop the RecordAsync call from CompleteNodeAsync, and no record is appended.
    ///     Red: pass run.CurrentSeq - 1 instead of seq to the recorder, and the Seq assertion fails.
    ///     Red: write ReviewEvidenceKind instead of ReviewResponseKind, and the Kind assertion fails.
    ///     Red: serialize the value's ToString instead of the value, and the payload assertion fails.
    /// </summary>
    [Fact]
    public async Task An_implement_visit_with_a_response_records_it()
    {
        var run = RunAt("implement");
        var (inner, store, appended) = Build(run);

        await CompleteAsync(store, run, new(StringComparer.Ordinal)
        {
            [ReviewHandoff.SummaryKey] = "fixed it",
            [ReviewHandoff.ReviewResponseKey] = ReportedResponse(),
        });

        var record = appended.Should().ContainSingle().Subject;
        record.Kind.Should().Be(WorkflowRunRecord.ReviewResponseKind);
        record.Seq.Should().Be(ImplementSeq);
        record.RunId.Should().Be(run.Id);
        record.Node.Should().Be("implement");
        record.StartedById.Should().Be("starter-1");
        using var payload = JsonDocument.Parse(record.PayloadJson);
        payload.RootElement.GetProperty("response").GetRawText().Should().Be(ReportedResponse().GetRawText());
        inner.Completed.Should().NotBeNull("the visit still completes");
        inner.FailureMessage.Should().BeNull();
    }

    /// <summary>
    ///     An implement visit with no response, a null one, or a review node that reports one (the authorship rule strips it)
    ///     records nothing.
    ///     Red: record whenever the key is present, ignoring IsResponse, and the null and json-null rows fail.
    ///     Red: drop the string and array length checks from IsResponse, and the empty-string and empty-array rows fail.
    ///     Red: record off the raw result instead of the authored one, and the review row fails.
    /// </summary>
    [Theory]
    [InlineData("implement", "absent")]
    [InlineData("implement", "null")]
    [InlineData("implement", "json-null")]
    [InlineData("implement", "empty-string")]
    [InlineData("implement", "empty-array")]
    [InlineData("review", "response")]
    public async Task An_implement_visit_without_a_response_records_nothing(string node, string reported)
    {
        var run = RunAt(node);
        var (inner, store, appended) = Build(run);
        var report = new Dictionary<string, object?>(StringComparer.Ordinal) { [ReviewHandoff.SummaryKey] = "fixed it" };
        switch (reported)
        {
            case "null":
                report[ReviewHandoff.ReviewResponseKey] = null;
                break;
            case "json-null":
                report[ReviewHandoff.ReviewResponseKey] = JsonDocument.Parse("null").RootElement.Clone();
                break;
            case "empty-string":
                report[ReviewHandoff.ReviewResponseKey] = JsonDocument.Parse("\"\"").RootElement.Clone();
                break;
            case "empty-array":
                report[ReviewHandoff.ReviewResponseKey] = JsonDocument.Parse("[]").RootElement.Clone();
                break;
            case "response":
                report[ReviewHandoff.ReviewResponseKey] = ReportedResponse();
                break;
        }

        await CompleteAsync(store, run, report);

        appended.Should().BeEmpty();
        inner.Completed.Should().NotBeNull();
    }

    /// <summary>
    ///     A completion the inner store would refuse records nothing: one whose seq is behind the run's, as a lost-lease
    ///     delivery that finishes after its redelivery is, and one for a run no longer Running. Otherwise its answer would be
    ///     the last record at that seq, the one the run view shows, though the run never accepted it.
    ///     Red: drop the IsAcceptable check from CompleteNodeAsync, and both rows append a record.
    ///     Red: drop the seq comparison from IsAcceptable, and the stale row appends one.
    ///     Red: drop the status comparison from IsAcceptable, and the failed-run row appends one.
    /// </summary>
    [Theory]
    [InlineData(ImplementSeq + 1, WorkflowStatus.Running)]
    [InlineData(ImplementSeq, WorkflowStatus.Failed)]
    public async Task A_completion_the_store_would_refuse_records_nothing(long currentSeq, WorkflowStatus status)
    {
        var run = RunAt("implement", currentSeq, status);
        var (_, store, appended) = Build(run);

        await CompleteAsync(store, run, new(StringComparer.Ordinal) { [ReviewHandoff.ReviewResponseKey] = ReportedResponse() });

        appended.Should().BeEmpty();
    }

    /// <summary>
    ///     A response that cannot be recorded fails the completion: the run fails with a message naming the record, and the
    ///     inner store never completes the node.
    ///     Red: ignore the recorder's failure in CompleteNodeAsync, and Completed is set and no failure is recorded.
    ///     Red: swallow the exception in the recorder and return success, and the same two assertions fail.
    /// </summary>
    [Fact]
    public async Task A_response_that_cannot_be_recorded_fails_the_completion()
    {
        var run = RunAt("implement");
        var (inner, store, _) = Build(run, new InvalidOperationException("database down"));

        await CompleteAsync(store, run, new(StringComparer.Ordinal) { [ReviewHandoff.ReviewResponseKey] = ReportedResponse() });

        inner.Completed.Should().BeNull("a visit whose answer is lost must not read as answered");
        inner.FailureMessage.Should().Contain("review_response").And.Contain("database down");
    }

    private static (ReviewResponseRecorder Recorder, List<WorkflowRunRecord> Appended) Recorder()
    {
        var appended = new List<WorkflowRunRecord>();
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            appended.Add(call.Arg<WorkflowRunRecord>());
            return ValueTask.CompletedTask;
        });
        return (TestNodeUsage.Responses(records), appended);
    }

    private static object Nested(int levels)
    {
        object value = "leaf";
        for (var i = 0; i < levels; i++)
            value = new List<object?> { value };
        return value;
    }

    /// <summary>
    ///     A response outside the JSON-shaped contract is returned as a failure naming its type, never thrown, and nothing is
    ///     appended.
    ///     Red: remove the catch around ToPayloadJson in RecordAsync, and the ArgumentException escapes the call.
    /// </summary>
    [Fact]
    public async Task A_response_that_is_not_JSON_shaped_is_a_failure_not_a_throw()
    {
        var (recorder, appended) = Recorder();

        var result = await recorder.RecordAsync(Guid.NewGuid(), ImplementSeq, "implement", "starter-1", Guid.Empty, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("System.Guid");
        appended.Should().BeEmpty();
    }

    /// <summary>
    ///     64 nested lists pass the contract's depth guard, but the payload's own wrapper object makes the 65th level, which
    ///     the serializer refuses. That too is a failure, not a throw.
    ///     Red: narrow the catch in RecordAsync to ArgumentException alone, and the serializer's exception escapes the call.
    /// </summary>
    [Fact]
    public async Task A_response_nested_one_level_too_deep_for_the_payload_is_a_failure()
    {
        var (recorder, appended) = Recorder();

        var result = await recorder.RecordAsync(Guid.NewGuid(), ImplementSeq, "implement", "starter-1", Nested(64), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("review_response");
        appended.Should().BeEmpty();
    }

    /// <summary>
    ///     Through the store, the refused response fails the run with the recorder's message instead of throwing out of the
    ///     completion.
    ///     Red: remove the catch around ToPayloadJson in RecordAsync, and CompleteNodeAsync throws before FailAsync runs.
    /// </summary>
    [Fact]
    public async Task A_response_that_is_not_JSON_shaped_fails_the_run()
    {
        var run = RunAt("implement");
        var (inner, store, appended) = Build(run);

        await CompleteAsync(store, run, new(StringComparer.Ordinal)
        {
            [ReviewHandoff.SummaryKey] = "fixed it",
            [ReviewHandoff.ReviewResponseKey] = Guid.Empty,
        });

        appended.Should().BeEmpty();
        inner.Completed.Should().BeNull();
        inner.FailureMessage.Should().Contain("review_response").And.Contain("System.Guid");
    }
}
