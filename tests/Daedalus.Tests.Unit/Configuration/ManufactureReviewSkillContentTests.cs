using System.Text.RegularExpressions;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     A content guard over <c>skills/manufacture-review/SKILL.md</c>, standing where phase 2.2's hollow
///     approval came from. Version 2 of that skill instructed the reviewer to pass a change when the evidence
///     about it came back empty; phase 2.2's close-out named that fallback as the reason its one successful run
///     approved work it had almost certainly never read. Phase 2.3 deleted it. This test is what stops it
///     returning quietly — a reviewer's rubric is prose, and prose has no compiler.
/// </summary>
/// <remarks>
///     <b>A negative assertion alone would be vacuous.</b> "The file does not contain X" passes for an empty
///     file, a deleted file, or a file rewritten into something else entirely. Every absence assertion here is
///     therefore paired with positive ones over the same file: it exists, it is substantial, and it carries the
///     rule that replaced the deleted one plus all three lens names. Both halves have to hold.
/// </remarks>
public sealed class ManufactureReviewSkillContentTests
{
    private static string ReviewSkill()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "skills", "manufacture-review", "SKILL.md");
        File.Exists(path).Should().BeTrue("skills/**/SKILL.md must be a Content item in Daedalus.Api.csproj");
        return File.ReadAllText(path);
    }

    /// <summary>
    ///     Lower-cases, strips markdown emphasis characters and collapses runs of whitespace, so the guard is not
    ///     defeated by a line re-wrap, a bolded word, or a change of case — the three ways this instruction would
    ///     most plausibly come back without anyone intending to smuggle it.
    /// </summary>
    private static string Normalize(string text) =>
        Whitespace.Replace(text.Replace("*", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal), " ")
            .ToLowerInvariant();

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.None, TimeSpan.FromSeconds(1));

    [Fact]
    public void The_review_skill_is_substantial_and_carries_its_rubric()
    {
        var normalized = Normalize(ReviewSkill());

        // The positive half. Without these, the absence assertions below would pass on an empty file.
        normalized.Length.Should().BeGreaterThan(2000, "a three-lens rubric is not a paragraph");
        normalized.Should().Contain("correctness");
        normalized.Should().Contain("falsifiability");
        normalized.Should().Contain("mechanism");

        // The rule that replaced the deleted fallback, stated as a rule rather than implied by its absence.
        // Falsifiable: delete the "Never approve on absence" section and this goes red even though the
        // absence assertions below would still be satisfied.
        normalized.Should().Contain("never approve on absence");
        normalized.Should().Contain("if you cannot see the work, that is a rejection or a failure");
    }

    [Theory]
    // The deleted instruction, in the fragments it is recognisable by. Each is a phrase from the verbatim
    // version 2 text; matching on fragments rather than the whole sentence means a partial reintroduction is
    // caught too. Falsifiable, and verified so: pasting the version 2 sentence back into the skill turns every
    // one of these red.
    [InlineData("treat it as approved")]
    [InlineData("rather than rejecting on an absence")]
    [InlineData("returns nothing at all")]
    [InlineData("absence you cannot attribute to the work")]
    public void The_review_skill_never_tells_the_reviewer_to_approve_on_absent_evidence(string forbidden)
    {
        Normalize(ReviewSkill()).Should().NotContain(Normalize(forbidden),
            "phase 2.2's close-out traced its hollow approval to exactly this instruction; a reviewer must never " +
            "approve because it found nothing");
    }

    [Fact]
    public void The_implement_skill_says_what_a_run_leaves_behind_and_why_it_cannot_commit()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "skills", "manufacture-implement", "SKILL.md");
        File.Exists(path).Should().BeTrue();
        var normalized = Normalize(File.ReadAllText(path));

        // The implementer now edits a working tree and nothing reverts its edits. Falsifiable: delete the
        // "What your run leaves behind" section and this goes red.
        normalized.Should().Contain("a failed run is not a no-op");
        normalized.Should().Contain("human step");

        // And the mechanism claim must stay the true one. The git and repo-action families are ABSENT from the
        // implementer's tool list, which is a different thing from being denied by policy - phase 2.2 shipped
        // nine instances of that confusion and this skill is where it would land next.
        normalized.Should().Contain("absent from your tool list");
        normalized.Should().NotContain("git__* is denied");
    }
}
