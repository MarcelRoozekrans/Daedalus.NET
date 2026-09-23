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
}
