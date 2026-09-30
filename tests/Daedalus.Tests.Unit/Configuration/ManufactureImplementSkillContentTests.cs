using System.Text.RegularExpressions;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     A content guard over <c>skills/manufacture-implement/SKILL.md</c>, the procedure the <c>implement</c> node's
///     agent is handed. A skill is prose, and prose has no compiler: phase 2.3's final review found the design
///     document corrected about what the implementer could write while this file still said the old thing. Phase 2.5
///     task B14 changed what it can write again, to this run's own worktree through <c>workspace__*</c>, so the guard
///     moved here from <see cref="ManufactureReviewSkillContentTests"/> and now pins the new surface.
/// </summary>
/// <remarks>
///     Every absence assertion is paired with positive ones over the same file, so an empty or gutted file cannot
///     satisfy it.
/// </remarks>
public sealed partial class ManufactureImplementSkillContentTests
{
    private static string ImplementSkill()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "skills", "manufacture-implement", "SKILL.md");
        File.Exists(path).Should().BeTrue("skills/**/SKILL.md must be a Content item in Daedalus.Api.csproj");
        return File.ReadAllText(path);
    }

    /// <summary>
    ///     Strips markdown emphasis and code characters and collapses whitespace; every assertion compares ignoring
    ///     case, so a re-wrap, a bolded word or a change of case does not defeat the guard.
    /// </summary>
    private static string Normalize(string text) =>
        Whitespace().Replace(text.Replace("*", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal), " ");

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();

    /// <summary>
    ///     The implementer edits through the four workspace tools. Red: deleting any one tool's name from the skill
    ///     turns its row red.
    /// </summary>
    [Theory]
    [InlineData("workspace__read_file")]
    [InlineData("workspace__list_files")]
    [InlineData("workspace__write_file")]
    [InlineData("workspace__edit_file")]
    public void The_implement_skill_names_each_workspace_tool(string tool)
    {
        ImplementSkill().Should().Contain(tool);
    }

    /// <summary>
    ///     The write tools refuse <c>AGENT.md</c>, and the skill has to say so, or an implementer that learned
    ///     something spends its turn on a refused write instead of reporting <c>learnings</c>. Red: deleting
    ///     <c>AGENT.md</c> from the refused-paths sentence.
    /// </summary>
    [Fact]
    public void The_implement_skill_says_AGENT_md_is_refused_for_writing()
    {
        Normalize(ImplementSkill()).Should().ContainEquivalentOf("and AGENT.md are refused for writing");
    }

    /// <summary>
    ///     Phase 2.3's skill said the node could not author code or create a file, which was true of
    ///     <c>roslyn__apply_code_action</c> alone and is false with <c>workspace__write_file</c>. An agent told it cannot
    ///     create a file reports <c>blocked</c> on work it could do. Red for the absences: pasting the old sentence back.
    ///     Red for the positive: deleting what <c>workspace__write_file</c> does.
    /// </summary>
    [Fact]
    public void The_implement_skill_does_not_claim_the_node_cannot_create_a_file()
    {
        var normalized = Normalize(ImplementSkill());

        normalized.Should().ContainEquivalentOf("workspace__write_file creates or replaces a whole file");
        normalized.Should().NotContainEquivalentOf("cannot author new code");
        normalized.Should().NotContainEquivalentOf("create a file, or make any change Roslyn does not already offer");
        normalized.Should().NotContainEquivalentOf("it is not a general editor");
    }

    /// <summary>
    ///     Ruling R29: only <c>.cs</c> and <c>.md</c> are writable, and work that needs a project, props, targets or
    ///     config file is a <c>blocked</c> naming the file, never a workaround. Red: deleting the extension sentence, or
    ///     the blocked instruction.
    /// </summary>
    [Fact]
    public void The_implement_skill_says_only_cs_and_md_are_writable_and_other_files_report_blocked()
    {
        var normalized = Normalize(ImplementSkill());

        normalized.Should().ContainEquivalentOf("Only .cs and .md files are writable");
        normalized.Should().ContainEquivalentOf(".csproj");
        normalized.Should().ContainEquivalentOf("Directory.Build.props");
        normalized.Should().ContainEquivalentOf("report blocked, naming the file and the change it needs");
    }

    /// <summary>
    ///     The implementer still holds no git or repo-action tool; host code commits and publishes. The mechanism
    ///     claim must stay the true one, absence rather than denial. Red: deleting the absence sentence, deleting both
    ///     statements that host code commits, or writing "git__* is denied" into the file.
    /// </summary>
    [Fact]
    public void The_implement_skill_says_host_code_commits_and_the_git_tools_are_absent()
    {
        var normalized = Normalize(ImplementSkill());

        normalized.Should().ContainEquivalentOf("absent from your tool list");
        normalized.Should().ContainEquivalentOf("host code commits");
        // Normalized like the text, or the literal's '*' could never match text Normalize stripped it from, and this
        // absence would hold by construction. It did, in ManufactureReviewSkillContentTests, until B14 moved it here.
        normalized.Should().NotContainEquivalentOf(Normalize("git__* is denied"));
    }

    /// <summary>
    ///     Nothing reverts the implementer's edits inside a run, and a failed run keeps its worktree for a human.
    ///     Red: deleting the "What your run leaves behind" section.
    /// </summary>
    [Fact]
    public void The_implement_skill_says_what_a_run_leaves_behind()
    {
        var normalized = Normalize(ImplementSkill());

        normalized.Should().ContainEquivalentOf("a failed run is not a no-op");
        normalized.Should().ContainEquivalentOf("human step");
    }

    /// <summary>
    ///     The outcome tool's <c>variables</c> argument is the only channel out of the node. Each phrase asserted here
    ///     occurs only in the reporting section, "What you write down, and what the reviewer gets": the bare words
    ///     <c>variables</c>, <c>summary</c> and <c>files_touched</c> also occur in step 5, the <c>blocked</c> paragraph
    ///     and the <c>learnings</c> example, so asserting those would survive the section's deletion. Red: deleting the
    ///     reporting section, or asking for <c>files_touched</c> as a string.
    /// </summary>
    [Fact]
    public void The_implement_skill_tells_the_implementer_how_to_report_its_variables()
    {
        var normalized = Normalize(ImplementSkill());

        normalized.Should().ContainEquivalentOf(
            "Report summary and files_touched, and optionally rationale and learnings, as the variables argument of the same outcome-tool call");
        normalized.Should().ContainEquivalentOf("That single call is the only read path the engine has");
        normalized.Should().ContainEquivalentOf("json array of paths",
            "an array is element-truncated and a string is character-cut");
        normalized.Should().NotContainEquivalentOf("does not reach the reviewer through the run's variables today");
    }

    /// <summary>
    ///     <c>roslyn__apply_code_action</c> is still in the implementer's list, and it still writes nothing unless the
    ///     call passes <c>preview: false</c>. The call succeeds either way, so the skill must name the argument. Red:
    ///     deleting the argument from the Roslyn paragraph.
    /// </summary>
    [Fact]
    public void The_implement_skill_names_preview_false_for_the_code_action_tool()
    {
        Normalize(ImplementSkill()).Should().ContainEquivalentOf("preview: false");
    }
}
