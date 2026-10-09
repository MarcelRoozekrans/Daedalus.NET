using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Covers the evidence schema the <c>review</c> node's outcome tool enforces: an approval must say what it
///     checked, a rejection must say what is wrong and where. Phase 2.2's manufacturing run reached
///     <c>Succeeded</c> on an approval that was indistinguishable from a real one; these are the assertions that
///     make the difference structural instead of a matter of later reasoning.
/// </summary>
/// <remarks>
///     Both call sites are exercised: <see cref="ReviewEvidence.Validate"/> directly, and
///     <see cref="DaedalusReviewTools.ReportReviewOutcome"/>, which is the surface the model actually sees. A
///     validator that is right while the tool wired to it is not would leave the schema enforced nowhere the
///     reviewer can feel it.
/// </remarks>
public sealed class ReviewEvidenceTests
{
    private const string GoodFinding = """[{"file":"src/Daedalus.Agents/Workflow/ReviewLensRunner.cs","line":42,"scenario":"the second pass reuses the first pass's task text, so lens 2 is never applied"}]""";
    private const string GoodChecked = """["ReviewLensRunner.RunLensesAsync short-circuits on the first non-approval","ReviewHandoff.ProjectForReview walks the allow-list, not the variables"]""";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""["", "  "]""")]
    public void An_approval_that_checked_nothing_is_refused(string? checkedJson)
    {
        var result = ReviewEvidence.Validate("correctness", "approved", findingsJson: null, checkedJson: checkedJson);

        // Falsifiable: deleting the empty-checked rule from ReviewEvidence.Validate turns this red. Verified by
        // deleting it. The blank-strings case is the interesting one - without dropping blanks first, ["", " "]
        // is a non-empty array and would satisfy a naive count check, making the cheapest route past the
        // evidence requirement a list of empty strings.
        result.IsFailure.Should().BeTrue("an approval that examined nothing is the hollow approval this schema exists to catch");
        result.Error.Should().Contain("checked");
    }

    [Fact]
    public void An_approval_that_lists_what_it_checked_is_accepted()
    {
        var result = ReviewEvidence.Validate("correctness", "approved", findingsJson: null, checkedJson: GoodChecked);

        // The green half. Without it, the rule above could be satisfied by refusing every approval, which would
        // pass the test above and break the pipeline.
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.IsApproval.Should().BeTrue();
        result.Value.Checked.Should().HaveCount(2);
        result.Value.Lens.Should().Be("correctness");
    }

    [Theory]
    [InlineData(null, "carry at least one finding")]
    [InlineData("[]", "carry at least one finding")]
    [InlineData("""[{"line":42,"scenario":"it throws"}]""", "no 'file'")]
    [InlineData("""[{"file":"src/X.cs","scenario":"it throws"}]""", "positive 'line'")]
    [InlineData("""[{"file":"src/X.cs","line":0,"scenario":"it throws"}]""", "positive 'line'")]
    [InlineData("""[{"file":"src/X.cs","line":-3,"scenario":"it throws"}]""", "positive 'line'")]
    [InlineData("""[{"file":"src/X.cs","line":42}]""", "no 'scenario'")]
    [InlineData("""[{"file":"src/X.cs","line":42,"scenario":"   "}]""", "no 'scenario'")]
    public void A_rejection_without_a_file_a_line_and_a_scenario_is_refused(string? findingsJson, string expected)
    {
        var result = ReviewEvidence.Validate("mechanism", "rejected", findingsJson, checkedJson: null);

        // Falsifiable per rule, not per test: each row names one clause of ParseFindings, and deleting that
        // clause turns that row red while leaving the others green. Verified for the file, line and scenario
        // clauses by deleting each in turn.
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain(expected);
    }

    [Fact]
    public void A_rejection_with_a_located_finding_is_accepted()
    {
        var result = ReviewEvidence.Validate("mechanism", "rejected", GoodFinding, checkedJson: null);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.IsApproval.Should().BeFalse();
        result.Value.Findings.Should().ContainSingle();
        result.Value.Findings[0].Line.Should().Be(42);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("looks good to me")]
    [InlineData("")]
    [InlineData(null)]
    public void A_verdict_outside_the_closed_set_is_refused(string? verdict)
    {
        var result = ReviewEvidence.Validate("correctness", verdict, null, GoodChecked);

        // The values must be exactly the two the review node declares in processes/manufacture.yaml, because
        // the engine branches on them. A synonym that validated here would fail later, in the dispatcher,
        // after the turn was paid for.
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("approved");
    }

    [Fact]
    public void Malformed_evidence_json_is_refused_with_a_message_the_model_can_act_on()
    {
        var notAnArray = ReviewEvidence.Validate("correctness", "approved", null, """{"checked":"yes"}""");
        notAnArray.IsFailure.Should().BeTrue();
        notAnArray.Error.Should().Contain("JSON array");

        var brokenFindings = ReviewEvidence.Validate("correctness", "rejected", "[{file:", null);
        brokenFindings.IsFailure.Should().BeTrue();
        brokenFindings.Error.Should().Contain("findings");
    }

    private static readonly string[] NulEntry = ["a\0b"];

    private static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string Findings(int count, string file = "src/X.cs", string scenario = "it throws") =>
        Json(Enumerable.Range(0, count).Select(_ => new { file, line = 1, scenario }).ToArray());

    /// <summary>
    ///     The evidence is recorded as a <c>WorkflowRunRecord</c>, so what the model writes into it is bounded.
    ///     Each row's red is deleting its own clause from ReviewEvidence: the entry-count checks in ParseChecked
    ///     and ParseFindings, and the length and NUL checks in CheckEntry.
    /// </summary>
    [Theory]
    [InlineData("checked-count", "'checked' has 33 entries")]
    [InlineData("checked-length", "'checked' entry 1 is 1001 characters")]
    [InlineData("checked-nul", "'checked' entry 1 contains a NUL character")]
    [InlineData("findings-count", "'findings' has 33 entries")]
    [InlineData("file-length", "Finding 1's 'file' is 1001 characters")]
    [InlineData("scenario-length", "Finding 1's 'scenario' is 1001 characters")]
    [InlineData("file-nul", "Finding 1's 'file' contains a NUL character")]
    [InlineData("scenario-nul", "Finding 1's 'scenario' contains a NUL character")]
    public void Evidence_that_could_not_be_recorded_in_full_is_refused(string row, string expected)
    {
        var tooLong = new string('x', ReviewEvidence.MaxEntryLength + 1);
        var result = row switch
        {
            "checked-count" => ReviewEvidence.Validate("correctness", "approved", null,
                Json(Enumerable.Range(0, ReviewEvidence.MaxEntries + 1).Select(i => $"item {i}").ToArray())),
            "checked-length" => ReviewEvidence.Validate("correctness", "approved", null, Json(new[] { tooLong })),
            "checked-nul" => ReviewEvidence.Validate("correctness", "approved", null, Json(NulEntry)),
            "findings-count" => ReviewEvidence.Validate("correctness", "rejected", Findings(ReviewEvidence.MaxEntries + 1), null),
            "file-length" => ReviewEvidence.Validate("correctness", "rejected", Findings(1, file: tooLong), null),
            "scenario-length" => ReviewEvidence.Validate("correctness", "rejected", Findings(1, scenario: tooLong), null),
            "file-nul" => ReviewEvidence.Validate("correctness", "rejected", Findings(1, file: "a\0b"), null),
            "scenario-nul" => ReviewEvidence.Validate("correctness", "rejected", Findings(1, scenario: "a\0b"), null),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain(expected);
    }

    /// <summary>
    ///     The green half of the bounds: evidence at every limit is accepted. Red: an off-by-one in any bound,
    ///     such as <c>&gt;=</c> for <c>&gt;</c>.
    /// </summary>
    [Fact]
    public void Evidence_at_every_limit_is_accepted()
    {
        var atLimit = new string('x', ReviewEvidence.MaxEntryLength);

        var approval = ReviewEvidence.Validate("correctness", "approved", null,
            Json(Enumerable.Range(0, ReviewEvidence.MaxEntries).Select(_ => atLimit).ToArray()));
        var rejection = ReviewEvidence.Validate("correctness", "rejected",
            Findings(ReviewEvidence.MaxEntries, file: atLimit, scenario: atLimit), null);

        approval.IsSuccess.Should().BeTrue(approval.IsFailure ? approval.Error : "");
        rejection.IsSuccess.Should().BeTrue(rejection.IsFailure ? rejection.Error : "");
    }

    /// <summary>
    ///     The tool's parameter descriptions tell the model the bounds before it reports. They are attribute strings,
    ///     so they hold the numbers as literals; this pins them to the constants. Red: changing
    ///     <see cref="ReviewEvidence.MaxEntries"/> or <see cref="ReviewEvidence.MaxEntryLength"/>.
    /// </summary>
    [Theory]
    [InlineData("findings")]
    [InlineData("checked")]
    public void The_tools_parameter_description_states_the_evidence_bounds(string parameter)
    {
        var description = typeof(DaedalusReviewTools)
            .GetMethod(nameof(DaedalusReviewTools.ReportReviewOutcome))!
            .GetParameters()
            .Single(p => string.Equals(p.Name, parameter, StringComparison.Ordinal))
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

        description.Should().Contain($"At most {ReviewEvidence.MaxEntries} entries");
        description.Should().Contain($"at most {ReviewEvidence.MaxEntryLength} characters");
        description.Should().Contain("no NUL characters");
    }

    [Fact]
    public void The_tool_the_reviewer_calls_refuses_a_hollow_approval_and_records_an_evidenced_one()
    {
        var tools = new DaedalusReviewTools();

        var refused = tools.ReportReviewOutcome("correctness", "approved", findings: null, @checked: "[]");
        // Falsifiable: making ReportReviewOutcome ignore the validator's failure and always answer "Recorded"
        // turns this red. Verified by doing exactly that.
        refused.Should().StartWith("Report refused:");
        refused.Should().Contain("checked");

        var accepted = tools.ReportReviewOutcome("correctness", "approved", findings: null, @checked: GoodChecked);
        accepted.Should().StartWith("Recorded:");
        accepted.Should().Contain("approved");

        var rejected = tools.ReportReviewOutcome("falsifiability", "rejected", GoodFinding, @checked: null);
        rejected.Should().StartWith("Recorded:");
        // The reviewer is told the remaining lenses will not run, so short-circuiting is visible to the agent
        // rather than only to the runner.
        rejected.Should().Contain("Remaining lenses will not run");
    }

    private const string OneDeferred =
        """[{"file":"src/A.cs","line":3,"title":"Cache never expires","scenario":"a stale entry is served after the TTL","reason":"different-area","existingIssue":12}]""";

    /// <summary>Red: ignore the deferredJson argument; Deferred is then empty.</summary>
    [Fact]
    public void An_approval_carries_its_deferred_findings()
    {
        var evidence = ReviewEvidence.Validate("correctness", "approved", null, """["read A.cs"]""", OneDeferred).Value;

        evidence.Deferred.Should().ContainSingle().Which.Should().Be(
            new DeferredFinding("src/A.cs", 3, "Cache never expires", "a stale entry is served after the TTL", "different-area", 12));
    }

    /// <summary>Red: drop the ReportReviewOutcome pass-through of deferred, or the deferred count in the message.</summary>
    [Fact]
    public void The_tool_passes_deferred_through_and_reports_its_count()
    {
        var tools = new DaedalusReviewTools();

        var accepted = tools.ReportReviewOutcome("correctness", "approved", null, """["read A.cs"]""", OneDeferred);

        accepted.Should().Contain("1 deferred");
    }

    /// <summary>D1: a rejection's findings are all fixed in the loop. Red: drop the rejection check; this validates.</summary>
    [Fact]
    public void A_rejection_may_not_defer()
    {
        var result = ReviewEvidence.Validate("correctness", "rejected", """[{"file":"a.cs","line":1,"scenario":"s"}]""", null, OneDeferred);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("only accepted with an approval");
    }

    /// <summary>Red: drop the matching check; that row then validates.</summary>
    [Theory]
    [InlineData("""[{"line":3,"title":"t","scenario":"s","reason":"blocked"}]""", "no 'file'")]
    [InlineData("""[{"file":"a.cs","line":0,"title":"t","scenario":"s","reason":"blocked"}]""", "positive 'line'")]
    [InlineData("""[{"file":"a.cs","line":3,"title":" ","scenario":"s","reason":"blocked"}]""", "no 'title'")]
    [InlineData("""[{"file":"a.cs","line":3,"title":"t","scenario":"","reason":"blocked"}]""", "no 'scenario'")]
    [InlineData("""[{"file":"a.cs","line":3,"title":"t","scenario":"s","reason":"later"}]""", "'reason'")]
    [InlineData("""[{"file":"a.cs","line":3,"title":"t","scenario":"s","reason":"blocked","existingIssue":0}]""", "'existingIssue'")]
    [InlineData("""[{"file":"a.cs","line":3,"title":"t","scenario":"s","reason":"blocked","existingIssue":"twelve"}]""", "optional numeric 'existingIssue'")]
    [InlineData("[null]", "is null; each entry must be an object")]
    [InlineData("""[{"file":"a.cs","line":3,"title":"t <!-- x","scenario":"s","reason":"blocked"}]""", "'title' contains '<!--'")]
    [InlineData("""[{"file":"/etc/a.cs","line":3,"title":"t","scenario":"s","reason":"blocked"}]""", "relative to the repository root")]
    [InlineData("""[{"file":"\\src\\a.cs","line":3,"title":"t","scenario":"s","reason":"blocked"}]""", "relative to the repository root")]
    [InlineData("""[{"file":"C:/src/a.cs","line":3,"title":"t","scenario":"s","reason":"blocked"}]""", "relative to the repository root")]
    [InlineData("""[{"file":"../../../x/y","line":3,"title":"t","scenario":"s","reason":"blocked"}]""", "relative to the repository root")]
    [InlineData("""[{"file":"src\\..\\..\\y.cs","line":3,"title":"t","scenario":"s","reason":"blocked"}]""", "relative to the repository root")]
    public void A_hollow_deferred_entry_is_refused(string deferred, string expected)
    {
        var result = ReviewEvidence.Validate("correctness", "approved", null, """["x"]""", deferred);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain(expected);
    }

    /// <summary>
    ///     Only a whole <c>..</c> segment climbs out; dots inside a name do not. Red: refuse any file containing "..";
    ///     these rows then fail.
    /// </summary>
    [Theory]
    [InlineData("src/a..b.cs")]
    [InlineData("src/..hidden/a.cs")]
    public void A_file_with_dots_inside_a_name_is_accepted(string file)
    {
        var deferred = $$"""[{"file":"{{file}}","line":3,"title":"t","scenario":"s","reason":"blocked"}]""";

        var result = ReviewEvidence.Validate("correctness", "approved", null, """["x"]""", deferred);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
    }

    /// <summary>Red: skip the title-length check; this validates.</summary>
    [Fact]
    public void A_title_over_200_characters_is_refused()
    {
        var deferred = $$"""[{"file":"a.cs","line":3,"title":"{{new string('t', 201)}}","scenario":"s","reason":"blocked"}]""";

        var result = ReviewEvidence.Validate("correctness", "approved", null, """["x"]""", deferred);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("200");
    }

    /// <summary>Red: skip the count check; 33 entries validate.</summary>
    [Fact]
    public void More_than_32_deferred_entries_are_refused()
    {
        var entry = """{"file":"a.cs","line":3,"title":"t","scenario":"s","reason":"blocked"}""";
        var deferred = "[" + string.Join(',', Enumerable.Repeat(entry, 33)) + "]";

        var result = ReviewEvidence.Validate("correctness", "approved", null, """["x"]""", deferred);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("32");
    }
}
