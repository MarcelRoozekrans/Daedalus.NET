using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>Phase 2.7: the latest review visit, the one publish and the deferred findings both read.</summary>
public sealed class ApprovingReviewTests
{
    private static readonly Guid RunId = new(0x4d0c8a8e, 0x1f1a, 0x4c55, 0x9a, 0x3e, 0x0f, 0x6e, 0x7d, 0x2c, 0x1b, 0x11);

    private static WorkflowRunRecord Evidence(long seq, string lens, string verdict, string deferred = "[]") =>
        WorkflowRunRecord.Create(RunId, seq, "review", WorkflowRunRecord.ReviewEvidenceKind, "workflow:run/review", null,
            $$"""{ "lens": "{{lens}}", "verdict": "{{verdict}}", "checked": ["x"], "findings": [], "deferred": {{deferred}} }""",
            DateTime.UtcNow).Value;

    private static string Deferred(string title) =>
        $$"""[{"file":"src/A.cs","line":3,"title":"{{title}}","scenario":"s","reason":"different-area"}]""";

    /// <summary>Red: answer a visit with no lenses instead of null; Value is then not null.</summary>
    [Fact]
    public void No_evidence_is_null_not_a_failure() =>
        ApprovingReview.Read([]).Value.Should().BeNull();

    /// <summary>
    ///     Ids are lens-n in lens order. Red: number across lenses instead of within each; the second id becomes
    ///     falsifiability-2.
    /// </summary>
    [Fact]
    public void Deferred_findings_get_stable_ids_per_lens()
    {
        var visit = ApprovingReview.Read([
            Evidence(9, "correctness", "approved", Deferred("one")),
            Evidence(9, "falsifiability", "approved", Deferred("two")),
            Evidence(9, "mechanism", "approved"),
        ]).Value!;

        visit.Approved.Should().BeTrue();
        visit.Deferred.Select(d => d.Id).Should().Equal("correctness-1", "falsifiability-1");
    }

    /// <summary>
    ///     A finding deferred in an earlier visit, which ended in a rejection, points at code since edited. Red: read
    ///     every seq instead of the highest; "stale" then appears.
    /// </summary>
    [Fact]
    public void Only_the_latest_visit_counts()
    {
        var visit = ApprovingReview.Read([
            Evidence(5, "correctness", "approved", Deferred("stale")),
            Evidence(5, "falsifiability", "rejected"),
            Evidence(9, "correctness", "approved", Deferred("fresh")),
        ]).Value!;

        visit.Deferred.Should().ContainSingle().Which.Finding.Title.Should().Be("fresh");
    }

    /// <summary>Red: treat any visit as approved; Approved is then true and Deferred non-empty.</summary>
    [Fact]
    public void A_latest_visit_with_a_rejection_is_not_approved_and_defers_nothing()
    {
        var visit = ApprovingReview.Read([
            Evidence(9, "correctness", "approved", Deferred("x")),
            Evidence(9, "falsifiability", "rejected"),
        ]).Value!;

        visit.Approved.Should().BeFalse();
        visit.RejectedLens.Should().Be("falsifiability");
        visit.Deferred.Should().BeEmpty();
    }

    /// <summary>A redelivered lens records again at the same seq; the last record is its word. Red: keep the first.</summary>
    [Fact]
    public void The_last_record_per_lens_wins()
    {
        var visit = ApprovingReview.Read([
            Evidence(9, "correctness", "approved", Deferred("first")),
            Evidence(9, "correctness", "approved", Deferred("second")),
        ]).Value!;

        visit.Deferred.Should().ContainSingle().Which.Finding.Title.Should().Be("second");
    }

    /// <summary>Records written before phase 2.7 carry no "deferred". Red: require the property; this fails.</summary>
    [Fact]
    public void A_record_without_deferred_reads_as_none()
    {
        var old = WorkflowRunRecord.Create(RunId, 9, "review", WorkflowRunRecord.ReviewEvidenceKind, "p", null,
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [] }""", DateTime.UtcNow).Value;

        ApprovingReview.Read([old]).Value!.Deferred.Should().BeEmpty();
    }

    private static WorkflowRunRecord Raw(string payload) =>
        WorkflowRunRecord.Create(RunId, 9, "review", WorkflowRunRecord.ReviewEvidenceKind, "p", null, payload, DateTime.UtcNow).Value;

    private static string WithDeferred(string deferred) =>
        $$"""{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": {{deferred}} }""";

    /// <summary>Red: treat a non-array deferred as none; the read succeeds and loses the finding.</summary>
    [Fact]
    public void A_deferred_that_is_not_a_list_is_a_failure() =>
        ApprovingReview.Read([Raw(WithDeferred("""{"file":"a"}"""))]).IsFailure.Should().BeTrue();

    /// <summary>Red: keep null entries; the read succeeds with a null finding that fails downstream.</summary>
    [Fact]
    public void A_null_deferred_entry_is_a_failure() =>
        ApprovingReview.Read([Raw(WithDeferred("[null]"))]).IsFailure.Should().BeTrue();

    /// <summary>Red: drop the completeness check; an entry with no title reads as a finding with a null title.</summary>
    [Fact]
    public void A_deferred_entry_missing_a_member_is_a_failure() =>
        ApprovingReview.Read([Raw(WithDeferred("""[{"file":"a.cs","line":3,"scenario":"s","reason":"different-area"}]"""))])
            .IsFailure.Should().BeTrue();

    /// <summary>Red: accept a non-positive line.</summary>
    [Fact]
    public void A_deferred_entry_with_no_line_is_a_failure() =>
        ApprovingReview.Read([Raw(WithDeferred("""[{"file":"a.cs","line":0,"title":"t","scenario":"s","reason":"different-area"}]"""))])
            .IsFailure.Should().BeTrue();

    /// <summary>Red: coerce or skip a wrong-typed member instead of failing.</summary>
    [Fact]
    public void A_deferred_entry_with_a_wrong_typed_member_is_a_failure() =>
        ApprovingReview.Read([Raw(WithDeferred("""[{"file":"a.cs","line":"three","title":"t","scenario":"s","reason":"different-area"}]"""))])
            .IsFailure.Should().BeTrue();

    /// <summary>Red: a JSON null deferred fails instead of reading as none.</summary>
    [Fact]
    public void A_null_deferred_reads_as_none() =>
        ApprovingReview.Read([Raw(WithDeferred("null"))]).Value!.Deferred.Should().BeEmpty();

    /// <summary>Red: skip the shape check; a non-object payload throws or reads as a visit.</summary>
    [Theory]
    [InlineData("[1]")]
    [InlineData("""{ "lens": "correctness", "checked": [], "deferred": [] }""")]
    public void An_unreadable_payload_is_a_failure_naming_the_record(string payload)
    {
        var result = ApprovingReview.Read([Raw(payload)]);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("review evidence record");
    }

    /// <summary>Red: append a redelivered lens at the end; ids then list falsifiability before correctness.</summary>
    [Fact]
    public void A_redelivered_lens_keeps_its_original_position()
    {
        var visit = ApprovingReview.Read([
            Evidence(9, "correctness", "approved", Deferred("a")),
            Evidence(9, "falsifiability", "approved", Deferred("b")),
            Evidence(9, "correctness", "approved", Deferred("c")),
        ]).Value!;

        visit.Deferred.Select(d => d.Id).Should().Equal("correctness-1", "falsifiability-1");
        visit.Deferred[0].Finding.Title.Should().Be("c");
    }
}
