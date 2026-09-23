using Daedalus.Agents.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Covers the variable contract between <c>implement</c> and <c>review</c>: the reviewer is given
///     <c>work_intent</c> and <c>files_touched</c>, and is not given the implementer's <c>summary</c> or
///     <c>rationale</c>. This is the isolation the phase can actually inspect after the fact — the variables
///     are a database row, unlike the memory partitioning, which is why it is worth a dedicated guard.
/// </summary>
public sealed class ReviewHandoffTests
{
    private static Dictionary<string, object?> FullImplementOutput() => new(StringComparer.Ordinal)
    {
        [ReviewHandoff.WorkIntentKey] = "Make TaskRepository.ClaimNextAsync skip cancelled tasks",
        [ReviewHandoff.FilesTouchedKey] = "src/Daedalus.Infrastructure/Persistence/TaskRepository.cs",
        [ReviewHandoff.SummaryKey] = "Added a status filter to the claim query.",
        [ReviewHandoff.RationaleKey] = "Cancelled tasks were being claimed and immediately failed.",
    };

    [Fact]
    public void The_reviewer_is_given_the_work_intent_and_the_files_touched()
    {
        var projected = ReviewHandoff.ProjectForReview(FullImplementOutput());

        // Falsifiable: dropping work_intent or files_touched from ReviewHandoff.ReviewReads turns this red -
        // and that is the realistic regression, because the temptation when the reviewer gets too much context
        // is to trim the allow-list rather than the source.
        projected.Should().ContainKey(ReviewHandoff.WorkIntentKey);
        projected[ReviewHandoff.WorkIntentKey].Should().Be("Make TaskRepository.ClaimNextAsync skip cancelled tasks");
        projected.Should().ContainKey(ReviewHandoff.FilesTouchedKey);
        projected[ReviewHandoff.FilesTouchedKey].Should().Be("src/Daedalus.Infrastructure/Persistence/TaskRepository.cs");
    }

    [Fact]
    public void The_reviewer_is_not_given_the_implementers_summary_or_rationale()
    {
        var projected = ReviewHandoff.ProjectForReview(FullImplementOutput());

        // Falsifiable: adding either key to ReviewHandoff.ReviewReads turns this red. Verified by doing exactly
        // that. Asserted key by key rather than as a count, so the message names which field leaked.
        projected.Should().NotContainKey(ReviewHandoff.SummaryKey,
            "a reviewer reading the implementer's account reviews the account, not the artifact");
        projected.Should().NotContainKey(ReviewHandoff.RationaleKey,
            "withholding the rationale while passing the summary would be the same leak, compressed");
        projected.Should().HaveCount(2, "the projection is an allow-list of exactly the two declared read keys");
    }

    [Fact]
    public void A_key_nobody_declared_is_excluded_by_default_rather_than_admitted()
    {
        var variables = FullImplementOutput();
        variables["implementer_confidence"] = "high";
        variables["payload"] = "{}";

        var projected = ReviewHandoff.ProjectForReview(variables);

        // This is the difference between an allow-list and a deny-list, and it is the assertion that would go
        // red if ProjectForReview were ever rewritten to walk the variables and remove known-bad keys: a filter
        // written that way admits every field a later phase adds, silently.
        projected.Should().NotContainKey("implementer_confidence");
        projected.Should().NotContainKey("payload");
        projected.Keys.Should().BeEquivalentTo([ReviewHandoff.WorkIntentKey, ReviewHandoff.FilesTouchedKey]);
    }

    [Fact]
    public void A_declared_key_the_run_does_not_carry_is_absent_rather_than_blank()
    {
        var projected = ReviewHandoff.ProjectForReview(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ReviewHandoff.WorkIntentKey] = "Do the thing",
        });

        // "Not supplied" and "supplied as nothing" must stay distinguishable: the review skill's rule is that an
        // absence of evidence is a rejection, and a projection that manufactured an empty string would hide the
        // absence behind a value. Falsifiable: seeding missing keys with "" turns this red.
        projected.Should().NotContainKey(ReviewHandoff.FilesTouchedKey);
        projected.Should().ContainSingle();
    }

    /// <summary>
    ///     The one declared key set left, and the only one that was ever load-bearing:
    ///     <see cref="ReviewHandoff.ProjectForReviewNode"/> walks it, so widening it widens what a review
    ///     dispatch is given.
    /// </summary>
    /// <remarks>
    ///     <c>ImplementWrites</c>, <c>ReviewWrites</c> and the three verdict key constants were deleted in the
    ///     final review's finding 7: no production code read them, and the assertion that used to live here
    ///     compared each one to the literal it had just been declared with. That is a test of the assignment
    ///     operator, not of a contract. The write sides are enforced where they are declared — the implement
    ///     skill's outcome-tool contract, and <c>ReviewEvidence.Validate</c> with
    ///     <c>DaedalusReviewTools.ReportReviewOutcome</c>'s argument names — and <c>ReviewEvidenceTests</c>
    ///     covers those against behaviour rather than against a restatement.
    /// </remarks>
    [Fact]
    public void The_reviewers_read_contract_is_the_one_the_design_states()
    {
        // Falsifiable: adding a key turns this red, which is the point - the set decides what the reviewer is
        // given, and it is meant to change deliberately and visibly rather than by a passing edit.
        ReviewHandoff.ReviewReads.Should().BeEquivalentTo(["work_intent", "files_touched"]);

        // files_touched is the only thing the implementer writes that the reviewer reads. Stated as an assertion
        // because it is the whole shape of the handoff: a pointer travels, an account does not.
        ReviewHandoff.ReviewReads.Should().NotContain(ReviewHandoff.SummaryKey);
        ReviewHandoff.ReviewReads.Should().NotContain(ReviewHandoff.RationaleKey);
    }
}
