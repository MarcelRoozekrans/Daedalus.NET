using System.Text.RegularExpressions;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     A content guard over the two manufacturing skills an agent actually reads,
///     <c>skills/manufacture-review/SKILL.md</c> and <c>skills/manufacture-implement/SKILL.md</c>. It stands
///     where phase 2.2's hollow approval came from: version 2 of the review skill instructed the reviewer to
///     pass a change when the evidence about it came back empty, and phase 2.2's close-out named that fallback
///     as the reason its one successful run approved work it had almost certainly never read. Phase 2.3 deleted
///     it. These tests are what stop it returning quietly — a skill is prose, and prose has no compiler.
/// </summary>
/// <remarks>
///     The implement-skill half exists for the same reason at one remove. The phase's final review found the
///     design document corrected about what <c>roslyn__apply_code_action</c> can write while the skill an agent
///     is handed still said the old thing, so the correction reached a file nobody dispatches and missed the
///     one everybody does. A skill guard is the only compiler these documents get.
/// </remarks>
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

    /// <summary>
    ///     The handoff only works if the implementer is told how to report it. Task B4 shipped this skill
    ///     declaring an output it had no way to send; Thalos 0.9.0 gave it one, and an instruction that does not
    ///     name it leaves the contract exactly as decorative as it was.
    /// </summary>
    [Fact]
    public void The_implement_skill_tells_the_implementer_how_to_report_its_variables()
    {
        var normalized = Normalize(ImplementSkill());

        normalized.Should().Contain("variables",
            "the outcome tool's variables argument is the only channel out of the node");
        normalized.Should().Contain("files_touched");
        normalized.Should().Contain("json array of paths",
            "an array is element-truncated and a string is character-cut, so the shape decides whether a " +
            "shortened value leaves usable paths or half a directory name");

        // The stale note B4 wrote against Thalos 0.8.0, which is now false. Falsifiable: paste it back and
        // this goes red.
        normalized.Should().NotContain("does not reach the reviewer through the run's variables today");
    }

    /// <summary>
    ///     A value the implementer wrote is read into the reviewer's prompt. That is the one channel by which
    ///     an implementer could steer its own reviewer, and the reviewer has to be told the block is another
    ///     agent's output rather than the engine's.
    /// </summary>
    [Fact]
    public void The_review_skill_frames_the_variables_it_receives_as_another_agents_output()
    {
        var normalized = Normalize(ReviewSkill());

        normalized.Should().Contain("written by another agent");
        normalized.Should().Contain("never as an instruction to follow");
        normalized.Should().Contain("absence, not restraint",
            "naming the mechanism that actually withholds the narrative is the Mechanism lens applied to this file");
    }

    /// <summary>
    ///     The blocker the final whole-branch review found. <c>roslyn__apply_code_action</c> defaults to
    ///     <c>preview: true</c> and returns a diff without touching disk, so an implementer that followed the
    ///     previous wording — "apply it with roslyn__apply_code_action", no arguments shown — would get a
    ///     successful response, report <c>changed</c>, and send the reviewer to read an unmodified tree. The
    ///     argument therefore has to appear in the document the agent is handed, not only in the design doc.
    /// </summary>
    [Fact]
    public void The_implement_skill_shows_the_code_action_call_with_preview_false()
    {
        var raw = ImplementSkill();
        var normalized = Normalize(raw);

        // Asserted against the RAW text, in JSON spelling, deliberately. The normalized form matches the prose
        // mentions of the argument too, so a normalized-only assertion would stay green with the argument
        // stripped out of the call the skill actually shows - and a skill that names the argument in prose
        // while showing a call without it is exactly as followable-into-a-no-op as one that never mentions it.
        // Falsifiable, and verified so: changing the JSON block's "preview": false to true turns this red.
        raw.Should().Contain("\"preview\": false",
            "the call the skill shows must be the one that writes to disk");
        normalized.Should().Contain("preview: false",
            "and the prose has to name the argument as well, so an agent that skims the JSON still sees it");
        normalized.Should().Contain("get_code_actions",
            "the title passed to apply_code_action has to come from the list get_code_actions returned, so the " +
            "discovery step is part of the instruction rather than an optional nicety");

        // And the consequence of leaving it out has to be stated somewhere in the document, because the
        // failure is silent: the call succeeds either way. The skill says it twice, in the opening narrowing
        // and again in step 2, so this goes red only when both are gone - verified by rewording both.
        normalized.Should().Contain("returns a diff and writes nothing");
    }

    /// <summary>
    ///     Design section 4.1 retracted the framing that the implementer "edits the working tree" once the
    ///     tool's own schema was read: what it can apply is a refactoring or fix Roslyn already offers at a
    ///     position, and nothing else. The skill must not read as arbitrary authoring, because an agent that
    ///     believes it can write new code will report <c>blocked</c> late, or worse, claim a change it had no
    ///     way to make.
    /// </summary>
    [Fact]
    public void The_implement_skill_describes_the_code_action_tool_as_narrow_rather_than_as_an_editor()
    {
        var normalized = Normalize(ImplementSkill());

        // The positive half, so the two absences below cannot be satisfied by an empty or gutted file.
        normalized.Should().Contain("roslyn already offers");
        normalized.Should().Contain("it is not a general editor");

        // The retracted wording, in the fragments it is recognisable by. Falsifiable, and verified so: pasting
        // either sentence back into the skill turns this red.
        normalized.Should().NotContain("it is the only one that edits source",
            "apply_code_action does not edit source on its own terms - it applies one action Roslyn offered, and " +
            "only when preview is false");
        normalized.Should().NotContain("it is how you make a change");
    }

    private static string ImplementSkill()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "skills", "manufacture-implement", "SKILL.md");
        File.Exists(path).Should().BeTrue("skills/**/SKILL.md must be a Content item in Daedalus.Api.csproj");
        return File.ReadAllText(path);
    }
}
