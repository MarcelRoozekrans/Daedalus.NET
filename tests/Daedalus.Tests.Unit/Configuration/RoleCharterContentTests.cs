namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Ruling R13: the chartered roles' prose must describe the tools each role holds. The skill is not the only prose
///     an agent reads - its charter body is its system prompt - and after phase 2.5 task B9 the implementer holds
///     <c>workspace__*</c> and the reviewer the two workspace read tools. A charter that still said "edit only through
///     <c>roslyn__apply_code_action</c>" or "inspect through Roslyn only" would send a live run down a path its skill
///     contradicts. Reads <c>roles/</c> from the repository root, found by walking up to <c>Daedalus.sln</c> as
///     <c>CleanArchitectureTests.FindRepositoryRoot</c> does.
/// </summary>
public sealed class RoleCharterContentTests
{
    private static string Charter(string role)
    {
        var path = Path.Combine(FindRepositoryRoot(), "roles", $"{role}.md");
        File.Exists(path).Should().BeTrue($"roles/{role}.md is the {role}'s charter");
        return File.ReadAllText(path);
    }

    /// <summary>Red: restoring the phase 2.4 body, which names no workspace tool, turns every row red.</summary>
    [Theory]
    [InlineData("workspace__read_file")]
    [InlineData("workspace__list_files")]
    [InlineData("workspace__write_file")]
    [InlineData("workspace__edit_file")]
    public void The_implementer_charter_names_each_workspace_tool(string tool)
    {
        Charter("implementer").Should().Contain(tool);
    }

    /// <summary>Red: restoring the phase 2.4 body, or deleting AGENT.md from the refused list.</summary>
    [Fact]
    public void The_implementer_charter_says_AGENT_md_is_refused()
    {
        Charter("implementer").Should().Contain("AGENT.md, .git/ and anything outside the worktree are refused for writing");
    }

    /// <summary>Ruling R29. Red: deleting the extension sentence, or its blocked clause.</summary>
    [Fact]
    public void The_implementer_charter_says_only_cs_and_md_are_writable_and_other_work_reports_blocked()
    {
        var charter = Charter("implementer");

        charter.Should().Contain("Only .cs and .md files are writable");
        charter.Should().Contain("reports blocked");
    }

    /// <summary>Red: restoring the phase 2.4 body, which names neither read tool.</summary>
    [Theory]
    [InlineData("workspace__read_file")]
    [InlineData("workspace__list_files")]
    public void The_reviewer_charter_names_each_workspace_read_tool(string tool)
    {
        Charter("reviewer").Should().Contain(tool);
    }

    /// <summary>
    ///     The reviewer names <c>workspace__write_file</c> only as a tool it does not hold. Red: restoring the phase 2.4
    ///     body, or dropping the "no" and so claiming the tool.
    /// </summary>
    [Fact]
    public void The_reviewer_charter_names_the_write_tool_only_as_one_it_does_not_hold()
    {
        Charter("reviewer").Should().Contain("no workspace__write_file");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Daedalus.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"Could not find Daedalus.sln walking up from {AppContext.BaseDirectory}.");
    }
}
