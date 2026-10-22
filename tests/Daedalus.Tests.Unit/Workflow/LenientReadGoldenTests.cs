using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Review Focus 1: model output is read leniently today, through <c>JsonSerializerDefaults.Web</c>. A line number sent
///     as a string still parses, and a key in any casing still matches. Captured before phase 3.2 moves
///     <see cref="ReviewEvidence"/> and <see cref="ApprovingReview"/> to a JSON context, which must keep both.
/// </summary>
public sealed class LenientReadGoldenTests
{
    /// <summary>
    ///     A finding with its line as a string, and keys in upper case and in lower case. Red: set
    ///     <c>NumberHandling = JsonNumberHandling.Strict</c> on <c>DaedalusRecordsWebJsonContext</c>; <c>"12"</c> no
    ///     longer reads as a number and the two string-line cases are refused. Red: set
    ///     <c>PropertyNameCaseInsensitive = false</c> there instead; the two cases with capitalised keys are refused.
    /// </summary>
    [Theory]
    [InlineData("""[{"file":"src/A.cs","line":"12","scenario":"it throws"}]""")]
    [InlineData("""[{"FILE":"src/A.cs","LINE":12,"SCENARIO":"it throws"}]""")]
    [InlineData("""[{"File":"src/A.cs","Line":"12","Scenario":"it throws"}]""")]
    public void Review_evidence_reads_a_string_line_and_any_key_casing(string findingsJson)
    {
        var result = ReviewEvidence.Validate("correctness", "rejected", findingsJson, checkedJson: null);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Findings.Should().Equal(new ReviewFinding("src/A.cs", 12, "it throws"));
    }

    /// <summary>
    ///     The same for a deferred finding, whose <c>existingIssue</c> is a nullable number. Red: set
    ///     <c>PropertyNameCaseInsensitive = false</c> on <c>DaedalusRecordsWebJsonContext</c>; the keys are then
    ///     matched case-sensitively and <c>"FILE"</c> reads as no file. Red: set <c>NumberHandling =
    ///     JsonNumberHandling.Strict</c> there; the string line <c>"3"</c> is refused.
    /// </summary>
    [Theory]
    [InlineData("""[{"file":"src/B.cs","line":"3","title":"T","scenario":"s","reason":"blocked","existingIssue":"77"}]""")]
    [InlineData("""[{"FILE":"src/B.cs","LINE":3,"TITLE":"T","SCENARIO":"s","REASON":"blocked","EXISTINGISSUE":77}]""")]
    public void Review_evidence_reads_a_deferred_finding_leniently(string deferredJson)
    {
        var result = ReviewEvidence.Validate("correctness", "approved", null, """["ReviewLensRunner"]""", deferredJson);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Deferred.Should().Equal(new DeferredFinding("src/B.cs", 3, "T", "s", "blocked", 77));
    }

    /// <summary>
    ///     A stored evidence record whose findings and deferred findings carry string lines and upper-case keys reads as
    ///     the same evidence. Red: set <c>NumberHandling = JsonNumberHandling.Strict</c> on
    ///     <c>DaedalusRecordsWebJsonContext</c>; the string lines are refused and so is the record.
    /// </summary>
    [Fact]
    public void Approving_review_reads_stored_entries_leniently()
    {
        const string payload = """
            {"lens":"correctness","verdict":"rejected","checked":["x"],
             "findings":[{"FILE":"src/A.cs","line":"12","Scenario":"it throws"}],
             "deferred":[{"file":"src/B.cs","LINE":"3","title":"T","SCENARIO":"s","reason":"blocked","existingIssue":"77"}]}
            """;
        var record = WorkflowRunRecord.Create(
            Guid.NewGuid(), 4, "review", WorkflowRunRecord.ReviewEvidenceKind, "workflow:run/review", null, payload,
            new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc)).Value;

        var read = ApprovingReview.ReadLens(record);

        read.IsSuccess.Should().BeTrue(read.IsFailure ? read.Error : "");
        read.Value.Findings.Should().Equal(new ReviewFinding("src/A.cs", 12, "it throws"));
        read.Value.Deferred.Should().Equal(new DeferredFinding("src/B.cs", 3, "T", "s", "blocked", 77));
    }
}
