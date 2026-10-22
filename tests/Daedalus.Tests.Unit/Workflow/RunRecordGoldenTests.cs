using Daedalus.Agents.Decisions;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Golden bytes of every run-record payload Daedalus.Agents writes with reflection today, captured before phase 3.2
///     moves them to JSON contexts. Each expected value is a literal of today's output, never computed. The strings carry
///     the characters an encoder change would show: <c>&lt;</c>, <c>&amp;</c>, <c>'</c>, <c>é</c> and an emoji.
/// </summary>
public sealed class RunRecordGoldenTests
{
    private const string Awkward = "<a & 'é' 🚀>";

    private static readonly Guid RunId = new Guid(0x6a1f2e3d, 0x4c5b, 0x4a69, 0x8e, 0x7f, 0xd, 0x1c, 0x2b, 0x3a, 0x4e, 0x5f) /* 6a1f2e3d-4c5b-4a69-8e7f-0d1c2b3a4e5f */;

    /// <summary>
    ///     Red: pass <c>new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }</c> in
    ///     <c>RunRecordJson.TestResultPayload</c>; the escaped characters turn raw.
    /// </summary>
    [Fact]
    public void A_test_result_payload_is_written_as_captured()
    {
        var payload = RunRecordJson.TestResultPayload("test", "1", Awkward, 1234L);

        payload.Should().Be("""{"tool":"test","exit":"1","summary":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E","elapsedMs":1234}""");
    }

    /// <summary>
    ///     The exit code and summary <see cref="SandboxCallRecorder"/> parses from a sandbox result, written as it writes
    ///     them: <c>exit</c> stays a string, which <c>OpenPullRequestAction</c> reads, and the output after the marker
    ///     is left out. Red: write <c>elapsedMs</c> as <c>elapsed</c> in <c>TestResultPayload</c>; the name differs.
    /// </summary>
    [Fact]
    public void A_parsed_sandbox_result_is_written_as_captured()
    {
        var (exit, summary) = SandboxCallRecorder.Parse("exit: 1\n" + Awkward + "\n--- output\nignored");
        var payload = RunRecordJson.TestResultPayload("build", exit, summary, 0L);

        payload.Should().Be("""{"tool":"build","exit":"1","summary":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E","elapsedMs":0}""");
    }

    /// <summary>Red: add <c>DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull</c> in <c>SandboxRestorePayload</c>; <c>"detail":null</c> goes.</summary>
    [Fact]
    public void A_sandbox_restore_payload_is_written_as_captured()
    {
        var withDetail = RunRecordJson.SandboxRestorePayload("failed", Awkward);
        var withoutDetail = RunRecordJson.SandboxRestorePayload("failed", null);

        withDetail.Should().Be("""{"restore":"failed","detail":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"}""");
        withoutDetail.Should().Be("""{"restore":"failed","detail":null}""");
    }

    /// <summary>Red: pass a relaxed-escaping encoder in <c>WorkspaceWritePayload</c>; the path's escaped characters turn raw.</summary>
    [Fact]
    public void A_workspace_write_payload_is_written_as_captured()
    {
        var withPath = RunRecordJson.WorkspaceWritePayload("workspace__write_file", "src/" + Awkward + ".cs");
        var withoutPath = RunRecordJson.WorkspaceWritePayload("workspace__write_file", null);

        withPath.Should().Be("""{"tool":"workspace__write_file","path":"src/\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E.cs"}""");
        withoutPath.Should().Be("""{"tool":"workspace__write_file","path":null}""");
    }

    /// <summary>
    ///     The evidence a lens pass records: findings and deferred findings with and without an existing issue.
    ///     Red: rename the anonymous member <c>@checked</c> to <c>examined</c> in <c>ReviewEvidencePayload</c>; the
    ///     property is written as <c>examined</c>.
    /// </summary>
    [Fact]
    public void A_review_evidence_payload_is_written_as_captured()
    {
        var payload = RunRecordJson.ReviewEvidencePayload(
            "correctness",
            ReviewEvidence.Approved,
            [Awkward, "ReviewLensRunner.RecordAsync"],
            [new ReviewFinding("src/A.cs", 12, Awkward)],
            [
                new DeferredFinding("src/B.cs", 3, "A title", Awkward, "different-area"),
                new DeferredFinding("src/C.cs", 4, "Another", "it throws", "blocked", 77),
            ]);

        payload.Should().Be("""{"lens":"correctness","verdict":"approved","checked":["\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E","ReviewLensRunner.RecordAsync"],"findings":[{"file":"src/A.cs","line":12,"scenario":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"}],"deferred":[{"file":"src/B.cs","line":3,"title":"A title","scenario":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E","reason":"different-area","existingIssue":null},{"file":"src/C.cs","line":4,"title":"Another","scenario":"it throws","reason":"blocked","existingIssue":77}]}""");
    }

    /// <summary>
    ///     <see cref="ApprovingReview.ReadLens"/> reads back the bytes <see cref="A_review_evidence_payload_is_written_as_captured"/>
    ///     pins, every field. Red: read deferred findings with <c>JsonSerializerOptions.Default</c> in
    ///     <c>ApprovingReview</c>; the camelCase names no longer match, every field reads empty, and the record is
    ///     refused as incomplete.
    /// </summary>
    [Fact]
    public void The_captured_evidence_payload_reads_back_field_for_field()
    {
        var record = WorkflowRunRecord.Create(
            RunId, 4, "review", WorkflowRunRecord.ReviewEvidenceKind, "workflow:run/review", null, CapturedEvidence,
            new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc)).Value;

        var read = ApprovingReview.ReadLens(record);

        read.IsSuccess.Should().BeTrue(read.IsFailure ? read.Error : "");
        read.Value.Lens.Should().Be("correctness");
        read.Value.Approved.Should().BeTrue();
        read.Value.Checked.Should().Equal(Awkward, "ReviewLensRunner.RecordAsync");
        read.Value.Findings.Should().Equal(new ReviewFinding("src/A.cs", 12, Awkward));
        read.Value.Deferred.Should().Equal(
            new DeferredFinding("src/B.cs", 3, "A title", Awkward, "different-area"),
            new DeferredFinding("src/C.cs", 4, "Another", "it throws", "blocked", 77));
    }

    /// <summary>
    ///     <see cref="ReviewEvidence.Validate"/> parses the shape the model is asked for, every field. Red: rename
    ///     <c>DeferredFinding</c>'s <c>[JsonPropertyName("existingIssue")]</c> to <c>"existing_issue"</c>; the issue
    ///     then reads null.
    /// </summary>
    [Fact]
    public void Review_evidence_parses_the_documented_shape()
    {
        var result = ReviewEvidence.Validate(
            "correctness",
            "approved",
            findingsJson: null,
            checkedJson: """["ReviewLensRunner.RecordAsync"]""",
            deferredJson: """[{"file":"src/B.cs","line":3,"title":"A title","scenario":"it throws","reason":"blocked","existingIssue":77}]""");

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Deferred.Should().Equal(new DeferredFinding("src/B.cs", 3, "A title", "it throws", "blocked", 77));
    }

    /// <summary>
    ///     The refusal a model reads when its findings do not parse carries the serializer's own message, path and
    ///     position included. Red: catch the <c>JsonException</c> in <c>ParseFindings</c> and report a fixed text
    ///     instead of <c>ex.Message</c>; the message differs.
    /// </summary>
    [Fact]
    public void A_malformed_finding_is_refused_with_the_captured_message()
    {
        var result = ReviewEvidence.Validate("correctness", "rejected", """[{"file":"src/A.cs","line":true,"scenario":"x"}]""", null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("""'findings' must be a JSON array of objects with 'file', 'line' and 'scenario': The JSON value could not be converted to Daedalus.Agents.Workflow.ReviewFinding. Path: $[0].line | LineNumber: 0 | BytePositionInLine: 31.""");
    }

    /// <summary>
    ///     Red: pass <c>new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }</c> in
    ///     <c>FindingRecords.DroppedPayload</c>; <c>"by":null</c> goes from the second payload.
    /// </summary>
    [Fact]
    public void The_findings_dropped_payload_is_written_as_captured()
    {
        var named = FindingRecords.DroppedPayload(["correctness-1", "mechanism-2"], Awkward, "attempt-1");
        var unnamed = FindingRecords.DroppedPayload(["correctness-1"], null, "attempt-2");

        named.Should().Be("""{"attempt":"attempt-1","dropped":["correctness-1","mechanism-2"],"by":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"}""");
        unnamed.Should().Be("""{"attempt":"attempt-2","dropped":["correctness-1"],"by":null}""");
    }

    /// <summary>Red: pass a relaxed-escaping encoder in <c>FindingRecords.DropVoidedPayload</c>; the attempt's characters turn raw.</summary>
    [Fact]
    public void The_findings_drop_voided_payload_is_written_as_captured()
    {
        var payload = FindingRecords.DropVoidedPayload(Awkward);

        payload.Should().Be("""{"attempt":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"}""");
    }

    /// <summary>Red: write <c>url = filed.Url.OriginalString</c> in <c>FindingRecords.FiledPayload</c>; the unescaped path is written.</summary>
    [Fact]
    public void The_finding_filed_payload_is_written_as_captured()
    {
        var payload = FindingRecords.FiledPayload(
            new FiledFinding("correctness-1", FindingRecords.CreatedMode, 42, new Uri("https://github.com/o/r/issues/42?q=a b")));

        payload.Should().Be("""{"id":"correctness-1","mode":"created","issue":42,"url":"https://github.com/o/r/issues/42?q=a%20b"}""");
    }

    /// <summary>
    ///     A decision with Minos' answers, and one without. Red: drop the <c>JsonIgnore(WhenWritingNull)</c> from the
    ///     payload's <c>answers</c>; the second payload gains <c>"answers":null</c>.
    /// </summary>
    [Fact]
    public void The_node_selection_payload_is_written_as_captured()
    {
        var answered = NodeSelectionRecord.Serialize(new NodeSelection("cheap", ["workspace__*", Awkward], "hard", 2, null, 0.875)
        {
            Answers = new NodeAnswers(3, "Hard", 0.875, new Dictionary<string, double>(StringComparer.Ordinal) { ["roslyn"] = 0.1, ["sandbox"] = 0.8 }),
        });
        var unanswered = NodeSelectionRecord.Serialize(new NodeSelection("default-model", null, "default", 0, "error:" + Awkward, null));

        answered.Should().Be("""{"level":"hard","model":"cheap","tool_filter":["workspace__*","\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"],"kept_families":2,"fallback":null,"confidence":0.875,"answers":{"question_set":3,"demand":"Hard","demand_confidence":0.875,"families":{"roslyn":0.1,"sandbox":0.8}}}""");
        unanswered.Should().Be("""{"level":"default","model":"default-model","tool_filter":null,"kept_families":0,"fallback":"error:\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E","confidence":null}""");
    }

    /// <summary>
    ///     A payload written today reads back as the same decision. Red: rename the payload's <c>kept_families</c> JSON
    ///     name; the kept count reads 0.
    /// </summary>
    [Fact]
    public void The_captured_node_selection_payload_reads_back()
    {
        var read = NodeSelectionRecord.Parse(CapturedNodeSelection);

        read.Should().NotBeNull();
        read!.Model.Should().Be("cheap");
        read.ToolFilter.Should().Equal("workspace__*", Awkward);
        read.Level.Should().Be("hard");
        read.KeptFamilies.Should().Be(2);
        read.Fallback.Should().BeNull();
        read.Confidence.Should().Be(0.875);
        read.Answers.Should().NotBeNull();
        read.Answers!.QuestionSet.Should().Be(3);
        read.Answers.Demand.Should().Be("Hard");
        read.Answers.DemandConfidence.Should().Be(0.875);
        read.Answers.Families.Should().Equal(new Dictionary<string, double>(StringComparer.Ordinal) { ["roslyn"] = 0.1, ["sandbox"] = 0.8 });
    }

    /// <summary>Red: rename <c>work_intent</c> in <c>DecisionStateJson</c>'s record; the state Minos is sent differs.</summary>
    [Fact]
    public void The_minos_decision_state_is_written_as_captured()
    {
        var state = DecisionStateJson.Serialize("developer", "implement", 2, Awkward);

        state.Should().Be("""{"role":"developer","node":"implement","visit":2,"work_intent":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"}""");
    }

    private const string CapturedEvidence = """{"lens":"correctness","verdict":"approved","checked":["\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E","ReviewLensRunner.RecordAsync"],"findings":[{"file":"src/A.cs","line":12,"scenario":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"}],"deferred":[{"file":"src/B.cs","line":3,"title":"A title","scenario":"\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E","reason":"different-area","existingIssue":null},{"file":"src/C.cs","line":4,"title":"Another","scenario":"it throws","reason":"blocked","existingIssue":77}]}""";

    private const string CapturedNodeSelection = """{"level":"hard","model":"cheap","tool_filter":["workspace__*","\u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E"],"kept_families":2,"fallback":null,"confidence":0.875,"answers":{"question_set":3,"demand":"Hard","demand_confidence":0.875,"families":{"roslyn":0.1,"sandbox":0.8}}}""";
}
