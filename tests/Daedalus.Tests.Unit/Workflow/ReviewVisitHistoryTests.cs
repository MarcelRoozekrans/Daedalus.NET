using System.Text.Json;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.13 task 3: the run view's every review visit, grouped from the run's review-evidence records and paired with
///     the review-response record of the implement visit that answered it. The records are built in the shapes the host
///     writes: evidence as <see cref="ReviewLensRunner"/> serializes it, responses through <see cref="ReviewResponseRecord"/>.
/// </summary>
public sealed class ReviewVisitHistoryTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private static readonly ReviewFinding Finding = new("src/A.cs", 12, "the comparison ignores case");

    /// <summary>One lens pass's record, its payload built the way <c>ReviewLensRunner.RecordAsync</c> builds it.</summary>
    private static WorkflowRunRecord Evidence(long seq, string lens, string verdict, string[]? @checked = null, ReviewFinding[]? findings = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            lens,
            verdict,
            @checked = (IReadOnlyList<string>)(@checked ?? (Approves(verdict) ? ["looked at " + lens] : [])),
            findings = (IReadOnlyList<ReviewFinding>)(findings ?? (Approves(verdict) ? [] : [Finding])),
            deferred = (IReadOnlyList<DeferredFinding>)[],
        });
        return Record(seq, "review", WorkflowRunRecord.ReviewEvidenceKind, payload);
    }

    private static bool Approves(string verdict) => string.Equals(verdict, ReviewEvidence.Approved, StringComparison.Ordinal);

    private static WorkflowRunRecord Response(long seq, string json) =>
        Record(seq, "implement", WorkflowRunRecord.ReviewResponseKind,
            ReviewResponseRecord.ToPayloadJson(JsonDocument.Parse(json).RootElement.Clone()));

    private static WorkflowRunRecord Record(long seq, string node, string kind, string payload) =>
        WorkflowRunRecord.Create(RunId, seq, node, kind, "host", "starter", payload, DateTime.UtcNow).Value;

    /// <summary>
    ///     Three visits, newest first, each with its lenses in the order they ran. Seq 2 is one delivery that rejected on its
    ///     second lens, as the lens runner stops at a rejection. Seq 4 is a redelivered review: correctness approved, then
    ///     falsifiability rejected, then the redelivery's correctness pass rejected, so the last record per lens counts and
    ///     correctness is the first rejecting lens. Seq 6 approved on all three.
    ///     Red: order the visits by seq ascending, and the first visit is seq 2.
    ///     Red: take the verdict from the first lens instead of any lens, and seq 2 reads Approved.
    ///     Red: take the last rejecting lens instead of the first, and seq 4's RejectingLens is falsifiability.
    ///     Red: keep the first record per lens instead of the last, and seq 4's RejectingLens is falsifiability.
    ///     Red: map the findings to [] on every lens, and seq 2's falsifiability lens has none.
    ///     Red: map Checked to [] on every lens, and seq 6's first lens has nothing checked.
    /// </summary>
    [Fact]
    public void Review_visits_are_listed_newest_first_with_verdict_and_rejecting_lens()
    {
        IReadOnlyList<WorkflowRunRecord> evidence =
        [
            Evidence(2, "correctness", ReviewEvidence.Approved),
            Evidence(2, "falsifiability", ReviewEvidence.Rejected),
            Evidence(4, "correctness", ReviewEvidence.Approved),
            Evidence(4, "falsifiability", ReviewEvidence.Rejected),
            Evidence(4, "correctness", ReviewEvidence.Rejected, findings: [new ReviewFinding("src/B.cs", 3, "never fails")]),
            Evidence(6, "correctness", ReviewEvidence.Approved),
            Evidence(6, "falsifiability", ReviewEvidence.Approved),
            Evidence(6, "mechanism", ReviewEvidence.Approved),
        ];

        var (visits, error) = ReviewVisitHistory.Read(evidence, []);

        error.Should().BeNull();
        visits.Select(v => v.Seq).Should().Equal(6, 4, 2);
        visits[0].Verdict.Should().Be("Approved");
        visits[0].RejectingLens.Should().BeNull();
        visits[0].Lenses.Select(l => l.Lens).Should().Equal("correctness", "falsifiability", "mechanism");
        visits[0].Lenses[0].Checked.Should().Equal("looked at correctness");
        visits[1].Verdict.Should().Be("Rejected");
        visits[1].RejectingLens.Should().Be("correctness");
        visits[1].Lenses.Select(l => l.Lens).Should().Equal("correctness", "falsifiability");
        visits[1].Lenses[0].Findings.Should().Equal(new ReviewFindingView("src/B.cs", 3, "never fails"));
        visits[2].Verdict.Should().Be("Rejected");
        visits[2].RejectingLens.Should().Be("falsifiability");
        visits[2].Lenses[1].Findings.Should().Equal(new ReviewFindingView("src/A.cs", 12, "the comparison ignores case"));
        visits[2].Lenses[0].Findings.Should().BeEmpty();
    }

    /// <summary>
    ///     A rejected visit carries the response of the next implement visit: the earliest response record after it, and the
    ///     last record at that seq when a redelivery wrote two. A string array reads one entry per line.
    ///     Red: pair with the latest response after the visit instead of the earliest, and seq 2 gets seq 5's answer.
    ///     Red: pair with the response at or before the visit's seq, and seq 2 gets nothing.
    ///     Red: take the first record at a seq instead of the last, and seq 4 reads "first try".
    ///     Red: return GetRawText for every value, and the array reads as JSON text.
    /// </summary>
    [Fact]
    public void A_rejected_visit_carries_the_next_implement_response()
    {
        IReadOnlyList<WorkflowRunRecord> evidence =
        [
            Evidence(2, "correctness", ReviewEvidence.Rejected),
            Evidence(4, "correctness", ReviewEvidence.Rejected),
            Evidence(6, "correctness", ReviewEvidence.Approved),
        ];
        IReadOnlyList<WorkflowRunRecord> responses =
        [
            Response(3, """["addressed: src/A.cs:12 - added the case", "disputed: src/B.cs:3 - it fails"]"""),
            Response(5, "\"first try\""),
            Response(5, "\"second try\""),
        ];

        var (visits, error) = ReviewVisitHistory.Read(evidence, responses);

        error.Should().BeNull();
        visits.Single(v => v.Seq == 2).ReviewResponse.Should().Be("addressed: src/A.cs:12 - added the case\ndisputed: src/B.cs:3 - it fails");
        visits.Single(v => v.Seq == 4).ReviewResponse.Should().Be("second try");
        visits.Single(v => v.Seq == 6).ReviewResponse.Should().BeNull("an approval sends the run on, not back to implement");
    }

    /// <summary>
    ///     A rejected visit whose next implement visit recorded no response reads as none recorded, even when a later implement
    ///     visit, answering a later review, did record one; and so does the newest rejection, which no implement visit has
    ///     answered yet.
    ///     Red: drop the bound at the next review visit's seq, and seq 2 takes the response at seq 5.
    /// </summary>
    [Fact]
    public void A_visit_without_a_response_record_reads_as_none_recorded()
    {
        IReadOnlyList<WorkflowRunRecord> evidence =
        [
            Evidence(2, "correctness", ReviewEvidence.Rejected),
            Evidence(4, "correctness", ReviewEvidence.Rejected),
            Evidence(6, "correctness", ReviewEvidence.Rejected),
        ];

        var (visits, _) = ReviewVisitHistory.Read(evidence, [Response(5, "\"answer to seq 4\"")]);

        visits.Single(v => v.Seq == 2).ReviewResponse.Should().BeNull();
        visits.Single(v => v.Seq == 4).ReviewResponse.Should().Be("answer to seq 4");
        visits.Single(v => v.Seq == 6).ReviewResponse.Should().BeNull();
    }

    /// <summary>
    ///     An evidence or response record that cannot be read is named by seq in the error, and the records that can be read
    ///     are still listed. Seq 2's correctness lens rejected and is readable, but a record of the same visit is not, so the
    ///     visit reads Unreadable with no rejecting lens: the full set of its lenses is unknown.
    ///     Red: return no error when a record is unreadable, and the error is null.
    ///     Red: leave the response seqs out of the error, and "seq 3" is missing.
    ///     Red: return no visits when any record is unreadable, and seq 4 is gone.
    ///     Red: compute the verdict from the readable lenses only, and seq 2 reads Rejected by correctness.
    /// </summary>
    [Fact]
    public void An_unreadable_evidence_record_is_reported_not_dropped()
    {
        IReadOnlyList<WorkflowRunRecord> evidence =
        [
            Evidence(2, "correctness", ReviewEvidence.Rejected),
            Record(2, "review", WorkflowRunRecord.ReviewEvidenceKind, """{"lens":"mechanism"}"""),
            Evidence(4, "correctness", ReviewEvidence.Approved),
        ];
        IReadOnlyList<WorkflowRunRecord> responses = [Record(3, "implement", WorkflowRunRecord.ReviewResponseKind, """{"answer":1}""")];

        var (visits, error) = ReviewVisitHistory.Read(evidence, responses);

        error.Should().NotBeNull();
        error.Should().Contain("1 review-evidence record(s) could not be read, at seq 2");
        error.Should().Contain("1 review-response record(s) could not be read, at seq 3");
        visits.Select(v => v.Seq).Should().Equal(4, 2);
        visits[1].Verdict.Should().Be("Unreadable");
        visits[1].RejectingLens.Should().BeNull();
        visits[1].Lenses.Select(l => l.Lens).Should().Equal("correctness");
        visits[1].ReviewResponse.Should().BeNull("the one response record there is cannot be read");
    }

    /// <summary>
    ///     A visit whose rejecting lens's record cannot be read is never read as an approval: its readable lens approved, its
    ///     unreadable one is unknown, so it reads Unreadable, and it carries the answer implement gave after it, as a rejection
    ///     does.
    ///     Red: give a visit with an unreadable record the verdict of its readable lenses, and it reads Approved.
    ///     Red: pair only Rejected visits with an answer, and its ReviewResponse is null.
    /// </summary>
    [Fact]
    public void A_visit_that_lost_its_rejecting_lens_reads_unreadable_and_keeps_its_answer()
    {
        IReadOnlyList<WorkflowRunRecord> evidence =
        [
            Evidence(2, "correctness", ReviewEvidence.Approved),
            Record(2, "review", WorkflowRunRecord.ReviewEvidenceKind, "{\"lens\":\"falsifiability\",\"verdict\":\"rejected\"}"),
            Evidence(4, "correctness", ReviewEvidence.Approved),
        ];

        var (visits, _) = ReviewVisitHistory.Read(evidence, [Response(3, "\"fixed the test\"")]);

        var lost = visits.Single(v => v.Seq == 2);
        lost.Verdict.Should().Be("Unreadable");
        lost.RejectingLens.Should().BeNull();
        lost.Lenses.Select(l => l.Lens).Should().Equal("correctness");
        lost.ReviewResponse.Should().Be("fixed the test");
    }

    /// <summary>
    ///     A visit none of whose records can be read is still listed, with the Unreadable verdict and no lenses, and the error
    ///     counts both records but names their seq once.
    ///     Red: skip a visit with no readable lens, and seq 2 is missing.
    ///     Red: drop the Distinct from Describe, and the error names "seq 2, 2".
    /// </summary>
    [Fact]
    public void A_visit_whose_records_are_all_unreadable_is_listed_as_unreadable()
    {
        IReadOnlyList<WorkflowRunRecord> evidence =
        [
            Record(2, "review", WorkflowRunRecord.ReviewEvidenceKind, """{"lens":"correctness"}"""),
            Record(2, "review", WorkflowRunRecord.ReviewEvidenceKind, "[]"),
            Evidence(4, "correctness", ReviewEvidence.Approved),
        ];

        var (visits, error) = ReviewVisitHistory.Read(evidence, []);

        visits.Select(v => v.Seq).Should().Equal(4, 2);
        visits[1].Verdict.Should().Be("Unreadable");
        visits[1].Lenses.Should().BeEmpty();
        error.Should().Be("2 review-evidence record(s) could not be read, at seq 2.");
    }
}
