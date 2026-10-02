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
    ///     <c>AGENT.md</c> is a protected path, and the skill has to say so, or an implementer that learned something
    ///     spends its turn on a refused write instead of reporting <c>learnings</c>. Red: deleting <c>AGENT.md</c>
    ///     from the protected-path sentence.
    /// </summary>
    [Fact]
    public void The_implement_skill_says_AGENT_md_is_a_protected_path()
    {
        Normalize(ImplementSkill()).Should().ContainEquivalentOf("These are protected: .git/, AGENT.md,");
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
    ///     Phase 2.6: the write tools refuse the protected paths in both modes; publish refuses them again, and refuses
    ///     <c>.gitattributes</c> or <c>.gitmodules</c> at any depth, symlinks and submodule pointers, only for a
    ///     sandboxed run. A run that needs a protected path is blocked naming the file. Each assertion has its own Red,
    ///     applied to <c>skills/manufacture-implement/SKILL.md</c>:
    ///     <list type="bullet">
    ///         <item>The list run ".gitmodules, .github/, .gitlab-ci.yml" - Red: delete <c>.github/</c> from the
    ///             list. The bare literal also occurs in the local-mode example, so the list run is asserted.</item>
    ///         <item>"In both modes the write tools refuse these paths" - Red: delete the sentence, or say only
    ///             publish refuses them.</item>
    ///         <item>"so a run that needs one is blocked" - Red: delete that clause.</item>
    ///         <item>"In a sandbox, publish also refuses them again" - Red: drop "In a sandbox," so the publish
    ///             refusal reads as unscoped, or move it under an "in either mode" heading.</item>
    ///         <item>"so sub/.gitattributes is as refused as the root one" - Red: delete the any-depth clause.</item>
    ///         <item>"symlinks and git submodule pointers wherever they are" - Red: delete that clause.</item>
    ///         <item>"In local mode only .cs and .md files are writable, so none of those can be written" - Red:
    ///             delete the sentence, leaving local mode's publish unstated.</item>
    ///         <item>The protected bullet does not say "in either mode" - Red: restore the heading "in either mode".</item>
    ///         <item>"report blocked, naming the file and the change it needs" - Red: delete the instruction.</item>
    ///         <item>"In a sandbox any other file extension is writable" - Red: delete the sandbox extension bullet.</item>
    ///         <item>"Only .cs and .md files are writable there" - Red: delete the local-mode bullet, or drop
    ///             "there".</item>
    ///         <item>"so .github/x.md is not writable in local mode either" - Red: delete the sentence saying the
    ///             protected list still applies in local mode.</item>
    ///         <item>The absences and the bullet check - Red: paste back the old "Only .cs and .md files are writable
    ///             in this phase" sentence, or add any bullet that mentions ".cs and .md" without "local mode".</item>
    ///     </list>
    /// </summary>
    [Fact]
    public void The_implement_skill_names_the_protected_paths_and_no_longer_forbids_project_files()
    {
        var normalized = Normalize(ImplementSkill());

        normalized.Should().ContainEquivalentOf(".gitmodules, .github/, .gitlab-ci.yml");
        normalized.Should().ContainEquivalentOf("In both modes the write tools refuse these paths");
        normalized.Should().ContainEquivalentOf("so a run that needs one is blocked");
        normalized.Should().ContainEquivalentOf("In a sandbox, publish also refuses them again whatever the sandbox allowed");
        normalized.Should().ContainEquivalentOf("so sub/.gitattributes is as refused as the root one");
        normalized.Should().ContainEquivalentOf("and symlinks and git submodule pointers wherever they are");
        normalized.Should().ContainEquivalentOf("In local mode only .cs and .md files are writable, so none of those can be written");
        normalized.Should().NotContainEquivalentOf("Protected paths are not yours to change, in either mode");
        normalized.Should().ContainEquivalentOf("report blocked, naming the file and the change it needs");
        normalized.Should().ContainEquivalentOf("In a sandbox any other file extension is writable");
        normalized.Should().ContainEquivalentOf("Only .cs and .md files are writable there");
        normalized.Should().ContainEquivalentOf("so .github/x.md is not writable in local mode either");

        // The old bullet, in its exact words or reworded: every bullet that says .cs and .md must be the local-mode one,
        // never a stand-alone rule for every mode.
        normalized.Should().NotContainEquivalentOf("Only .cs and .md files are writable in this phase");
        var csMdBullets = ImplementSkill().Split("\n- ", StringSplitOptions.None)
            .Where(b => Normalize(b).Contains(".cs and .md", StringComparison.OrdinalIgnoreCase)).ToList();
        csMdBullets.Should().NotBeEmpty();
        csMdBullets.Should().OnlyContain(b => Normalize(b).Contains("Local mode", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     The implementer runs <c>sandbox__build</c> and <c>sandbox__test</c> before claiming <c>changed</c>, reads
    ///     the result, trusts the build over Roslyn diagnostics, and knows the restore facts. Each assertion has its
    ///     own Red, applied to <c>skills/manufacture-implement/SKILL.md</c>:
    ///     <list type="bullet">
    ///         <item>"run sandbox__build and then sandbox__test ..." - Red: delete the section's lead sentence.</item>
    ///         <item>"Read the exit code and the summary in each result" - Red: delete that sentence.</item>
    ///         <item>"Do not claim changed while the build or the tests fail" - Red: delete that sentence.</item>
    ///         <item>"with no --no-restore" - Red: delete the plain-dotnet bullet.</item>
    ///         <item>"sandbox__build is the authority on whether the change builds" - Red: delete that sentence.</item>
    ///         <item>"a package from any other source fails the build" - Red: delete that clause.</item>
    ///     </list>
    /// </summary>
    [Fact]
    public void The_implement_skill_says_to_build_and_test_before_claiming_changed()
    {
        var normalized = Normalize(ImplementSkill());

        normalized.Should().ContainEquivalentOf("run sandbox__build and then sandbox__test after your edits and before you report changed");
        normalized.Should().ContainEquivalentOf("Read the exit code and the summary in each result");
        normalized.Should().ContainEquivalentOf("Do not claim changed while the build or the tests fail");
        normalized.Should().ContainEquivalentOf("plain dotnet build or dotnet test with no --no-restore");
        normalized.Should().ContainEquivalentOf("sandbox__build is the authority on whether the change builds");
        normalized.Should().ContainEquivalentOf("a package from any other source fails the build");
    }

    /// <summary>
    ///     The skill is shared by both modes, so it must say there is no shell in either, not credit the absence to
    ///     the sandbox tools alone, which local mode does not have. Red: restoring "There is no shell, because
    ///     sandbox__build and sandbox__test each run one fixed command" turns the absence red; deleting "in either
    ///     mode" turns the first positive red; deleting "The sandbox tools take no command of yours" the second.
    /// </summary>
    [Fact]
    public void The_implement_skill_says_there_is_no_shell_in_either_mode()
    {
        var normalized = Normalize(ImplementSkill());

        normalized.Should().ContainEquivalentOf("There is no shell in either mode");
        normalized.Should().ContainEquivalentOf("The sandbox tools take no command of yours");
        normalized.Should().NotContainEquivalentOf("There is no shell, because");
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

    /// <summary>
    ///     The implementer's envelope names its Roslyn tools one by one, with no glob, and holds none of the operator
    ///     tools. A skill that still said the list was <c>roslyn__*</c> would tell the model it may call
    ///     <c>load_solution</c> or <c>trust_solution</c>. Red: pasting the old "Tools list is roslyn__*" sentence back
    ///     trips the absence; deleting the no-glob clause or the operator-tool clause trips its positive.
    /// </summary>
    [Fact]
    public void The_implement_skill_describes_the_enumerated_roslyn_envelope_not_a_wildcard()
    {
        var normalized = Normalize(ImplementSkill());

        normalized.Should().ContainEquivalentOf("named Roslyn read tools plus roslyn__apply_code_action");
        normalized.Should().ContainEquivalentOf("exact names with no glob");
        normalized.Should().ContainEquivalentOf("operator tools such as loading, rebuilding or trusting a solution");
        normalized.Should().ContainEquivalentOf("background-task tools, are not in it either");
    }

    /// <summary>
    ///     The skill offers no Roslyn glob at all: the implementer's envelope is exact names. Red: pasting back the old
    ///     <c>roslyn__*</c> Tools sentence, or any <c>roslyn__get_*</c> style pattern, anywhere in the skill.
    /// </summary>
    [Fact]
    public void The_implement_skill_names_no_roslyn_glob()
    {
        RoslynGlob().Matches(ImplementSkill()).Select(m => m.Value).Should().BeEmpty();
    }

    [GeneratedRegex(@"roslyn__[a-z_]*\*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RoslynGlob();
}
