using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Results;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Covers <see cref="ReviewLensRunner"/>: the decorator that turns a <c>review</c> node's declared
///     <c>lenses:</c> into one agent turn per lens, stops at the first rejection, and projects the run's
///     variables down to the review contract's read keys before each pass.
/// </summary>
/// <remarks>
///     The short-circuit assertion is the one this suite exists for. Without it, losing the early return costs
///     three turns where one would do on every rejection — the run still succeeds, every other test still
///     passes, and the only symptom is a bill. That is the shape of defect this repository keeps shipping, so
///     the pass count is asserted as an exact number, never as "at least one".
/// </remarks>
public sealed class ReviewLensRunnerTests
{
    private static readonly AgentId ReviewerId = new(new Guid(0x62_21_a3_bd, 0x4f5c, 0x483b, 0xb2, 0xff, 0x36, 0xa2, 0xab, 0xa0, 0x7a, 0xef));
    private static readonly OutcomeToolSchema ReviewOutcome = new("workflow__report_outcome", ["approved", "rejected"]);

    private static readonly string[] ThreeLenses = ["correctness", "falsifiability", "mechanism"];

    private const string GoodChecked = """["ReviewLensRunner short-circuits","ReviewHandoff walks the allow-list"]""";
    private const string GoodFinding = """[{"file":"src/Daedalus.Agents/Workflow/ReviewLensRunner.cs","line":42,"scenario":"lens 2 reuses lens 1's task text"}]""";

    /// <summary>A review run positioned at the review node, carrying the full implement-step output.</summary>
    private static WorkflowRun ReviewRun(Dictionary<string, object?>? variables = null) => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 3,
        CurrentNode = "review",
        CurrentSeq = 4,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["review"] = 1 },
        Variables = variables ?? new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["work_intent"] = "Make ClaimNextAsync skip cancelled tasks",
            ["files_touched"] = "src/Daedalus.Infrastructure/Persistence/TaskRepository.cs",
            ["summary"] = "SUMMARY-MUST-NOT-REACH-THE-REVIEWER",
            ["rationale"] = "RATIONALE-MUST-NOT-REACH-THE-REVIEWER",
        },
    };

    private static IProcessDefinitionStore DefinitionsWith(params string[] lenses)
    {
        var definition = new ProcessDefinition
        {
            Name = "manufacture",
            Version = 3,
            StartNode = "implement",
            Nodes = new Dictionary<string, ProcessNode>(StringComparer.Ordinal)
            {
                ["implement"] = new() { Agent = "implementer", Skill = "manufacture-implement", Next = "review" },
                ["review"] = new()
                {
                    Agent = "reviewer",
                    Skill = "manufacture-review",
                    Outcomes = ["approved", "rejected"],
                    Branch = new Dictionary<string, string>(StringComparer.Ordinal) { ["approved"] = "done", ["rejected"] = "implement" },
                    Lenses = lenses.Length == 0 ? null : lenses,
                },
                ["done"] = new() { Terminal = "succeeded" },
            },
        };

        var store = Substitute.For<IProcessDefinitionStore>();
        store.GetAsync("manufacture", 3, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<ProcessDefinition>>(Result<ProcessDefinition>.Success(definition)));
        return store;
    }

    private static SubagentRunRequest ReviewRequest(WorkflowRun run) => new()
    {
        AgentId = ReviewerId,
        Task = "Load the skill 'manufacture-review' and follow it.",
        Caller = new WorkflowCaller(run, grant: null),
        RequiredOutcome = ReviewOutcome,
    };

    /// <summary>A turn that reported <paramref name="verdict"/> through both the evidence tool and the engine's.</summary>
    private static AgentTurnResult Turn(string lens, string verdict, string? findings = null, string? examined = null) =>
        new(TurnId.New(), new SessionId(Guid.Empty), $"{lens}: {verdict}", default,
        [
            new ToolCallSummary(
                ToolCallId.New(),
                DaedalusReviewTools.QualifiedReportReviewOutcomeToolName,
                System.Text.Json.JsonSerializer.Serialize(new { lens, verdict, findings, @checked = examined }),
                Succeeded: true, "Recorded", TimeSpan.FromMilliseconds(3)),
            new ToolCallSummary(
                ToolCallId.New(),
                ReviewOutcome.ToolName,
                $$"""{"{{OutcomeToolSchema.ArgumentName}}":"{{verdict}}"}""",
                Succeeded: true, "ok", TimeSpan.FromMilliseconds(1)),
        ], TimeSpan.FromSeconds(2));

    private static AgentTurnResult Approves(string lens) => Turn(lens, "approved", examined: GoodChecked);
    private static AgentTurnResult Rejects(string lens) => Turn(lens, "rejected", findings: GoodFinding);

    /// <summary>A record store whose every append throws <paramref name="failure"/>.</summary>
    private static IWorkflowRunRecordStore FailingRecords(Exception failure)
    {
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException(failure));
        return records;
    }

    /// <summary>Records every task text the decorator sent inward, in order.</summary>
    private sealed class RecordingRunner(Func<int, AgentTurnResult> respond) : ISubagentRunner
    {
        public List<string> Tasks { get; } = [];

        public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
        {
            Tasks.Add(request.Task);
            return ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Success(respond(Tasks.Count - 1)));
        }
    }

    [Fact]
    public async Task An_approval_runs_all_three_lenses_in_the_declared_order()
    {
        var lensNames = new[] { "correctness", "falsifiability", "mechanism" };
        var inner = new RecordingRunner(i => Approves(lensNames[i]));
        var runner = new ReviewLensRunner(inner, DefinitionsWith(lensNames), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");

        // Exactly three, not "at least three". Falsifiable in both directions: breaking out of the loop after
        // the first approval turns this red at 1, and running the lens list twice turns it red at 6.
        inner.Tasks.Should().HaveCount(3, "an approval must survive all three lenses");

        // And each pass was told which lens it was, in the declared order. Falsifiable: composing the task text
        // once outside the loop, or iterating the lenses in reverse, turns this red - and without it the count
        // above would still pass while every pass asked the same question.
        inner.Tasks[0].Should().Contain("Review pass 1 of 3: the correctness lens");
        inner.Tasks[1].Should().Contain("Review pass 2 of 3: the falsifiability lens");
        inner.Tasks[2].Should().Contain("Review pass 3 of 3: the mechanism lens");
    }

    [Fact]
    public async Task The_first_rejection_short_circuits_and_the_remaining_lenses_do_not_run()
    {
        var inner = new RecordingRunner(_ => Rejects("correctness"));
        var runner = new ReviewLensRunner(inner, DefinitionsWith("correctness", "falsifiability", "mechanism"), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // THE assertion. Falsifiable: deleting the `if (!evidence.Value.IsApproval) return last;` early return
        // in RunLensesAsync turns this red at 3. Verified by deleting it. Without this assertion that deletion
        // is invisible - the run still ends rejected, every other test here still passes, and the only symptom
        // is that every rejection costs three paid turns instead of one.
        inner.Tasks.Should().ContainSingle("a rejection already stops the pipeline; confirming it twice more buys nothing");
        inner.Tasks[0].Should().Contain("the correctness lens");

        // The turn handed back is the rejecting pass's own turn, so the outcome the dispatcher reads is the one
        // the model actually reported. Falsifiable: returning the first pass's result from a later index, or
        // fabricating a turn here, turns this red.
        result.Value.Text.Should().Be("correctness: rejected");
    }

    [Fact]
    public async Task A_rejection_on_the_second_lens_runs_two_passes_and_stops()
    {
        var inner = new RecordingRunner(i => i == 0 ? Approves("correctness") : Rejects("falsifiability"));
        var runner = new ReviewLensRunner(inner, DefinitionsWith("correctness", "falsifiability", "mechanism"), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        // Distinguishes "stops at the first rejection" from "stops after the first pass". Without this row the
        // short-circuit test above would also pass for a runner that only ever ran one lens.
        result.IsSuccess.Should().BeTrue();
        inner.Tasks.Should().HaveCount(2);
        result.Value.Text.Should().Be("falsifiability: rejected");
    }

    /// <summary>
    ///     Each pass names the keys it was given and restates none of their values. Task B5 moved the
    ///     withholding itself upstream into <see cref="ReviewHandoffWorkflowStore"/> - see that type and
    ///     <c>SquadHandoffEndToEndTests</c>, where the same claim is asserted against the task text a real
    ///     dispatcher built. What is left here is the property that keeps this decorator from reopening the
    ///     leak from below: it must not copy implementer-written values out of the engine's escaped, framed
    ///     variables block and into a section of its own.
    /// </summary>
    [Fact]
    public async Task Each_pass_names_the_keys_it_was_given_and_restates_none_of_their_values()
    {
        var inner = new RecordingRunner(i => Approves(ThreeLenses[i]));
        var runner = new ReviewLensRunner(inner, DefinitionsWith("correctness", "falsifiability", "mechanism"), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        inner.Tasks.Should().HaveCount(3);
        foreach (var task in inner.Tasks)
        {
            task.Should().Contain("work_intent").And.Contain("files_touched",
                "the reviewer has to be told which keys this run carried, or an absent one reads like an absent section");
            task.Should().NotContain("Make ClaimNextAsync skip cancelled tasks",
                "a value restated here is a second, unframed copy of text another agent wrote");
            task.Should().NotContain("src/Daedalus.Infrastructure/Persistence/TaskRepository.cs");
            task.Should().NotContain("SUMMARY-MUST-NOT-REACH-THE-REVIEWER");
            task.Should().NotContain("RATIONALE-MUST-NOT-REACH-THE-REVIEWER");
        }
    }

    [Fact]
    public async Task A_run_with_no_review_variables_is_told_so_rather_than_handed_an_empty_section()
    {
        var inner = new RecordingRunner(_ => Approves("correctness"));
        var runner = new ReviewLensRunner(inner, DefinitionsWith("correctness"), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        await runner.RunAsync(ReviewRequest(ReviewRun(new Dictionary<string, object?>(StringComparer.Ordinal))), CancellationToken.None);

        // An absence the reviewer cannot see is an absence it cannot act on, and acting on it is the rule that
        // replaced version 2's approve-on-absence fallback. Falsifiable: dropping the else-branch message turns
        // this red.
        inner.Tasks.Should().ContainSingle();
        inner.Tasks[0].Should().Contain("No review variables were supplied");
        inner.Tasks[0].Should().Contain("never approve on an absence");
    }

    [Fact]
    public async Task A_pass_that_reports_an_approval_with_no_evidence_fails_the_node()
    {
        // The model ignored the tool's refusal and reported approved through the engine tool anyway. Without the
        // read-side re-validation in ReadEvidence, the dispatcher would read `approved` off this turn and
        // advance the run to the gate.
        var hollow = Turn("correctness", "approved", examined: "[]");
        var inner = new RecordingRunner(_ => hollow);
        var runner = new ReviewLensRunner(inner, DefinitionsWith("correctness", "falsifiability", "mechanism"), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        // Falsifiable: deleting the ReviewEvidence.Validate call from ReadEvidence turns this red - the run
        // succeeds and returns the hollow approval. Verified by deleting it.
        result.IsFailure.Should().BeTrue("an approval the evidence schema refused must not reach the dispatcher as an approval");
        result.Error.Message.Should().Contain("checked");
        inner.Tasks.Should().ContainSingle("a pass that cannot be read is not a reason to run the remaining lenses");
    }

    [Fact]
    public async Task A_pass_that_reports_no_evidence_at_all_fails_the_node()
    {
        var silent = new AgentTurnResult(TurnId.New(), new SessionId(Guid.Empty), "approved, trust me", default,
        [
            new ToolCallSummary(ToolCallId.New(), ReviewOutcome.ToolName,
                $$"""{"{{OutcomeToolSchema.ArgumentName}}":"approved"}""", true, "ok", TimeSpan.Zero),
        ], TimeSpan.Zero);

        var runner = new ReviewLensRunner(new RecordingRunner(_ => silent), DefinitionsWith("correctness"), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        // The engine's own tool was called correctly, with a value in its closed set - exactly the shape of
        // phase 2.2's hollow approval. Falsifiable: treating a missing evidence call as an approval turns this
        // red.
        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain(DaedalusReviewTools.QualifiedReportReviewOutcomeToolName);
    }

    [Fact]
    public async Task A_pass_whose_two_reports_disagree_fails_the_node()
    {
        var disagreeing = new AgentTurnResult(TurnId.New(), new SessionId(Guid.Empty), "", default,
        [
            new ToolCallSummary(ToolCallId.New(), DaedalusReviewTools.QualifiedReportReviewOutcomeToolName,
                System.Text.Json.JsonSerializer.Serialize(new { lens = "correctness", verdict = "rejected", findings = GoodFinding }),
                true, "Recorded", TimeSpan.Zero),
            new ToolCallSummary(ToolCallId.New(), ReviewOutcome.ToolName,
                $$"""{"{{OutcomeToolSchema.ArgumentName}}":"approved"}""", true, "ok", TimeSpan.Zero),
        ], TimeSpan.Zero);

        var runner = new ReviewLensRunner(new RecordingRunner(_ => disagreeing), DefinitionsWith("correctness"), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        // The engine reads only its own tool, so without this check the run would advance to the gate on an
        // approval this runner had just seen rejected with findings. Falsifiable: deleting the agreement check
        // in ReadEvidence turns this red.
        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("must agree");
    }

    [Fact]
    public async Task A_pass_that_answers_a_different_lens_than_it_was_given_fails_the_node()
    {
        // One thorough correctness pass must not be able to stand in for all three.
        var inner = new RecordingRunner(_ => Approves("correctness"));
        var runner = new ReviewLensRunner(inner, DefinitionsWith("correctness", "mechanism"), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        // Pass 1 matches; pass 2 is told "mechanism" and answers "correctness". Falsifiable: dropping the lens
        // match in ParseCall turns this red.
        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("mechanism");
        inner.Tasks.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_node_that_declares_no_lenses_is_passed_straight_through_unmodified()
    {
        var inner = new RecordingRunner(_ => Approves("correctness"));
        var runner = new ReviewLensRunner(inner, DefinitionsWith(), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);
        var request = ReviewRequest(ReviewRun());

        var result = await runner.RunAsync(request, CancellationToken.None);

        // implement, publish, and every process that does not use the rubric must be untouched by this
        // decorator. Falsifiable: defaulting to ReviewLens.All when a node declares none turns this red at 3
        // and with a rewritten task text.
        result.IsSuccess.Should().BeTrue();
        inner.Tasks.Should().ContainSingle();
        inner.Tasks[0].Should().Be(request.Task, "a node with no lenses must get its request through unmodified");
    }

    [Fact]
    public async Task A_caller_that_is_not_a_workflow_run_is_passed_straight_through()
    {
        var inner = new RecordingRunner(_ => Approves("correctness"));
        var runner = new ReviewLensRunner(inner, DefinitionsWith("correctness", "falsifiability", "mechanism"), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var request = new SubagentRunRequest
        {
            AgentId = ReviewerId,
            Task = "a scheduled run, not a workflow node",
            Caller = new Daedalus.Agents.Scheduling.DetachedPrincipal("schedule:daedalus", ["reader"]),
        };

        var result = await runner.RunAsync(request, CancellationToken.None);

        // Falsifiable: matching on anything looser than "the caller is a WorkflowCaller" - a name prefix, say -
        // turns this red by rewriting a scheduled run's task text into a review pass.
        result.IsSuccess.Should().BeTrue();
        inner.Tasks.Should().ContainSingle();
        inner.Tasks[0].Should().Be("a scheduled run, not a workflow node");
    }

    [Fact]
    public async Task A_lens_the_host_does_not_know_fails_the_node_rather_than_being_skipped()
    {
        var inner = new RecordingRunner(_ => Approves("correctness"));
        var runner = new ReviewLensRunner(inner, DefinitionsWith("correctness", "thoroughness"), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        // A typo that quietly dropped one of three passes would make a two-lens approval indistinguishable from
        // a three-lens one. Falsifiable: filtering unknown names out of ReviewLens.Resolve instead of failing
        // turns this red - and note that no turn runs at all, so the failure costs nothing.
        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("thoroughness");
        inner.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_pass_stops_the_sequence_immediately()
    {
        var inner = Substitute.For<ISubagentRunner>();
        inner.RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<AgentTurnResult, AgentError>.Failure(AgentError.Validation("budget exhausted")));

        var runner = new ReviewLensRunner(inner, DefinitionsWith("correctness", "falsifiability", "mechanism"), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        // A turn that failed is not a verdict, and retrying it twice more on the next two lenses would pay three
        // times for the same failure. Falsifiable: continuing the loop past a failed pass turns this red at 3.
        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Be("budget exhausted");
        await inner.Received(1).RunAsync(Arg.Any<SubagentRunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_accepted_pass_is_recorded_as_the_caller_at_the_runs_seq_node_starter_and_clock_time()
    {
        var records = Substitute.For<IWorkflowRunRecordStore>();
        var appended = new List<WorkflowRunRecord>();
        records.AppendAsync(Arg.Do<WorkflowRunRecord>(appended.Add), Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 9, 30, 0, TimeSpan.Zero));
        var run = ReviewRun() with { StartedBy = new RunPrincipal("u-starter", ["developer"]) };
        var request = ReviewRequest(run);
        var runner = new ReviewLensRunner(
            new RecordingRunner(_ => Rejects("correctness")), DefinitionsWith("correctness"), RecordStoreScopes.For(records), clock,
            NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        var record = appended.Should().ContainSingle().Subject;
        // Each red: swapping the field for another value in ReviewLensRunner.RecordAsync.
        record.RunId.Should().Be(run.Id);
        record.Seq.Should().Be(4, "ReviewRun's CurrentSeq; recording CurrentSeq + 1 turns this red");
        record.Node.Should().Be("review");
        record.Kind.Should().Be(WorkflowRunRecord.ReviewEvidenceKind);
        record.PrincipalId.Should().Be(request.Caller.Id);
        record.StartedById.Should().Be("u-starter");
        record.CreatedAt.Should().Be(new DateTime(2026, 9, 28, 9, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task The_recorded_lens_is_the_configured_name_not_the_case_the_model_reported()
    {
        var records = Substitute.For<IWorkflowRunRecordStore>();
        var appended = new List<WorkflowRunRecord>();
        records.AppendAsync(Arg.Do<WorkflowRunRecord>(appended.Add), Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        // The lens match in ParseCall ignores case, so "Correctness" is accepted as the correctness pass.
        var runner = new ReviewLensRunner(
            new RecordingRunner(_ => Rejects("Correctness")), DefinitionsWith("correctness"), RecordStoreScopes.For(records),
            TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        var record = appended.Should().ContainSingle().Subject;
        using var payload = System.Text.Json.JsonDocument.Parse(record.PayloadJson);
        // Red: recording evidence.Lens, the model's spelling, instead of lens.Name.
        payload.RootElement.GetProperty("lens").GetString().Should().Be("correctness");
    }

    [Fact]
    public async Task A_pass_whose_evidence_cannot_be_recorded_fails_the_node_and_no_further_pass_runs()
    {
        var inner = new RecordingRunner(i => Approves(ThreeLenses[i]));
        var runner = new ReviewLensRunner(
            inner, DefinitionsWith("correctness", "falsifiability", "mechanism"),
            RecordStoreScopes.For(FailingRecords(new InvalidOperationException("database is down"))), TimeProvider.System,
            NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        // Red: ignoring RecordAsync's failure in RunLensesAsync. The review then approves on three passes of
        // which no evidence exists, which is the silent loss this failure mode exists to prevent.
        result.IsFailure.Should().BeTrue("a review whose evidence was lost must not read as a review that ran");
        result.Error.Code.Should().Be(AgentErrorCode.StoreError);
        result.Error.Message.Should().Contain("'correctness' review pass's evidence could not be recorded");
        // Red: remembering the failure and returning it after the loop lets all three passes run first.
        inner.Tasks.Should().ContainSingle("a pass that could not be recorded is not a reason to pay for the next");
    }

    [Fact]
    public async Task A_cancellation_nobody_asked_for_fails_the_node_rather_than_escaping()
    {
        // A command timeout surfaces as an OperationCanceledException while the caller's token is untouched.
        var runner = new ReviewLensRunner(
            new RecordingRunner(_ => Approves("correctness")), DefinitionsWith("correctness"),
            RecordStoreScopes.For(FailingRecords(new OperationCanceledException("command timeout"))), TimeProvider.System,
            NullLogger<ReviewLensRunner>.Instance);

        var act = async () => await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        // Red: filtering with `ex is not OperationCanceledException` alone lets the timeout escape.
        var result = (await act.Should().NotThrowAsync()).Subject;
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.StoreError);
    }

    [Fact]
    public async Task The_callers_own_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var runner = new ReviewLensRunner(
            new RecordingRunner(_ => Approves("correctness")), DefinitionsWith("correctness"),
            RecordStoreScopes.For(FailingRecords(new OperationCanceledException(cts.Token))), TimeProvider.System,
            NullLogger<ReviewLensRunner>.Instance);

        var act = async () => await runner.RunAsync(ReviewRequest(ReviewRun()), cts.Token);

        // Red: catching every exception turns the caller's own cancellation into a failed node.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    ///     Task B15: the one turn the dispatcher sees carries the usage of every lens pass, so the review node's
    ///     completion event is what the review cost. Red per assertion: returning the last pass's turn unchanged
    ///     reports 300 input tokens and 30 output tokens, and summing into a fresh usage that drops the cache counts
    ///     fails the cache assertion.
    /// </summary>
    [Fact]
    public async Task An_approval_reports_the_usage_of_all_three_passes()
    {
        var inner = new RecordingRunner(i => WithUsage(Approves(ThreeLenses[i]), i));
        var runner = new ReviewLensRunner(inner, DefinitionsWith(ThreeLenses), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.Usage.InputTokens.Should().Be(600);
        result.Value.Usage.OutputTokens.Should().Be(60);
        result.Value.Usage.CacheReadTokens.Should().Be(60);
    }

    /// <summary>
    ///     The rejection return path sums too: the passes that approved before the rejection were paid for. Red:
    ///     returning the rejecting pass's turn unchanged reports 200.
    /// </summary>
    [Fact]
    public async Task A_rejection_reports_the_usage_of_every_pass_that_ran()
    {
        var inner = new RecordingRunner(i => WithUsage(i == 0 ? Approves("correctness") : Rejects("falsifiability"), i));
        var runner = new ReviewLensRunner(inner, DefinitionsWith(ThreeLenses), RecordStoreScopes.For(), TimeProvider.System, NullLogger<ReviewLensRunner>.Instance);

        var result = await runner.RunAsync(ReviewRequest(ReviewRun()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        inner.Tasks.Should().HaveCount(2, "otherwise the sum below is over a different number of passes");
        result.Value.Usage.InputTokens.Should().Be(300);
    }

    /// <summary>Pass <paramref name="pass"/>'s turn with usage 100, 200 or 300 input tokens, a tenth of that out and 20 cache reads.</summary>
    private static AgentTurnResult WithUsage(AgentTurnResult turn, int pass) =>
        turn with { Usage = new TurnUsage((pass + 1) * 100, (pass + 1) * 10, "lens-model") { CacheReadTokens = 20 } };
}
