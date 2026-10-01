using System.Text.RegularExpressions;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Ruling R13: the chartered roles' prose must describe the tools each role holds. The skill is not the only prose
///     an agent reads - its charter body is its system prompt - and after phase 2.5 task B9 the implementer holds
///     <c>workspace__*</c> and the reviewer the two workspace read tools. A charter that still said "edit only through
///     <c>roslyn__apply_code_action</c>" or "inspect through Roslyn only" would send a live run down a path its skill
///     contradicts. Reads <c>roles/</c> from the repository root, found by walking up to <c>Daedalus.sln</c> as
///     <c>CleanArchitectureTests.FindRepositoryRoot</c> does.
/// </summary>
public sealed partial class RoleCharterContentTests
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
    ///     The reviewer names <c>workspace__write_file</c> only as a tool it does not hold. The first assertion shows it
    ///     is named as withheld; the second, that every mention is, so a charter that also told the reviewer to use it
    ///     fails. Red: restoring the phase 2.4 body, dropping the "no", or adding a sentence such as "fix typos with
    ///     workspace__write_file".
    /// </summary>
    [Fact]
    public void The_reviewer_charter_names_the_write_tool_only_as_one_it_does_not_hold()
    {
        var charter = Charter("reviewer");

        charter.Should().Contain("no workspace__write_file");
        var mentions = WriteToolMention().Count(charter);
        var negated = NegatedWriteToolMention().Count(charter);
        negated.Should().Be(mentions, "every mention of workspace__write_file must be as a tool the reviewer does not hold");
    }

    [GeneratedRegex(@"\bworkspace__write_file\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WriteToolMention();

    [GeneratedRegex(@"\bno\s+workspace__write_file\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NegatedWriteToolMention();

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

    /// <summary>
    ///     The charters' Roslyn sentences match the shipped envelopes: named read tools, not the <c>roslyn__*</c>
    ///     wildcard those envelopes dropped. Red: restoring either old sentence.
    /// </summary>
    [Theory]
    [InlineData("implementer", "Use the named roslyn__* read tools to understand the solution", "Use roslyn__* to understand the solution")]
    [InlineData("reviewer", "and with the named roslyn__* read tools", "and with the roslyn__* read tools")]
    public void The_charters_describe_named_roslyn_read_tools_not_the_wildcard(string role, string current, string stale)
    {
        var charter = Charter(role);

        charter.Should().Contain(current);
        charter.Should().NotContain(stale);
    }
}
