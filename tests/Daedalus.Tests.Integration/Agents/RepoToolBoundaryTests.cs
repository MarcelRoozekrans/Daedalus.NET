using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Security;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Tools;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     The write boundary between an unattended scheduled run and a repository it must never act on, pinned against
///     the real composed <c>Daedalus.Api</c> host rather than a hand-assembled fixture.
/// </summary>
/// <remarks>
///     <para>
///         Three layers are asserted here, and only the third actually holds:
///     </para>
///     <list type="number">
///         <item>
///             The write tools live in their own tool source, so the scout's <c>daedalus__*</c> glob cannot name
///             them. The source is called <c>repoaction</c> and not <c>daedalus_write</c> precisely because
///             <c>daedalus_write__comment</c> is one character away from being matched by accident.
///         </item>
///         <item>
///             The scout's configured <c>Tools</c> allow-list omits the write source. This protects only agents that
///             declare a list at all — <see cref="AgentDefinition.Tools"/> defaults to everything.
///         </item>
///         <item>
///             <c>repoaction__*</c> is bound to <see cref="DeveloperPolicy"/> in <c>Thalos:ToolPolicies</c>. A
///             scheduled run executes as <c>schedule:daedalus</c> with roles <c>["reader"]</c>, so
///             <c>DefaultToolAuthorizer</c> denies it whatever its tool list happens to say. The previous phase
///             shipped a write boundary enforced only by tool-surface absence and its own review called that
///             brittle; this layer is the fix. Those roles are configuration — <c>DetachedRuns:Roles</c> — so
///             <see cref="The_configured_detached_run_roles_fail_the_developer_policy"/> reads the shipped value
///             rather than a literal: without it, adding <c>developer</c> to that one line would delete the
///             boundary with every test here still green.
///         </item>
///     </list>
///     <para>
///         <b>Phase 2.1 added a second write source under the same boundary:</b> <c>git__*</c>
///         (Thalos's own <c>GitActionTools</c> - branch, commit, push, open pull request) is bound to
///         <see cref="DeveloperPolicy"/> the same way, and only layer 3 is checked for it here - layers 1 and 2
///         are <c>repoaction__*</c>-specific naming/allow-list decisions this tool source does not repeat.
///     </para>
///     <para>
///         <b>The matcher is Thalos's own.</b> <see cref="Glob"/> is the public matcher
///         <c>ToolCatalog</c> uses to filter <see cref="AgentDefinition.Tools"/> and that
///         <see cref="ToolPolicyBinding.Matches"/> uses to bind a policy to a tool. Reimplementing a wildcard match
///         here — however trivial it looks — would let every assertion below pass while the real authorizer behaved
///         differently, which is the one failure mode this file exists to rule out.
///     </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class RepoToolBoundaryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly IAgentRuntime _runtime = Substitute.For<IAgentRuntime>();
    private ApiWebApplicationFactory _factory = null!;
    private IReadOnlyList<string> _toolNames = [];

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _factory = new ApiWebApplicationFactory(fixture.ConnectionString, _runtime);
        _toolNames = await ReadToolNamesAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void The_read_tools_are_exposed_under_the_existing_daedalus_source()
    {
        ToolNames().Should().Contain("daedalus__repo_activity",
            "the scout already allows daedalus__*, so reads need no config change");
    }

    [Fact]
    public void The_write_tools_are_exposed_under_a_separate_source()
    {
        ToolNames().Should().Contain("repoaction__comment_on_issue");
    }

    [Fact]
    public void The_write_source_name_cannot_be_matched_by_the_scout_glob()
    {
        var writeTools = ToolNames().Where(n => n.StartsWith("repoaction__", StringComparison.Ordinal)).ToList();

        writeTools.Should().NotBeEmpty("otherwise this test passes vacuously");

        foreach (var name in writeTools)
        {
            GlobMatches("daedalus__*", name).Should().BeFalse(
                "a write tool one character away from the read prefix would be matched by accident");
        }
    }

    [Fact]
    public void The_scouts_configured_patterns_match_no_write_tool()
    {
        var scout = AgentDefinitions().Single(a => string.Equals(a.Name, "scout", StringComparison.Ordinal));
        var writeTools = ToolNames().Where(n => n.StartsWith("repoaction__", StringComparison.Ordinal)).ToList();

        writeTools.Should().NotBeEmpty("otherwise this test passes vacuously");

        foreach (var tool in writeTools)
        {
            scout.Tools.Any(p => GlobMatches(p, tool)).Should().BeFalse(
                $"the unattended scout must not reach {tool}");
        }
    }

    [Fact]
    public void Every_write_tool_is_bound_to_the_developer_policy()
    {
        var policies = ToolPolicyBindings();
        var writeTools = ToolNames().Where(n => n.StartsWith("repoaction__", StringComparison.Ordinal)).ToList();

        writeTools.Should().NotBeEmpty("otherwise this test passes vacuously");

        foreach (var tool in writeTools)
        {
            policies.Any(b => GlobMatches(b.ToolPattern, tool) && string.Equals(b.PolicyName, DeveloperPolicy.PolicyName, StringComparison.Ordinal))
                .Should().BeTrue($"{tool} must be denied at the authorizer, not only by a tool list");
        }
    }

    /// <summary>
    ///     Phase 2.1's own boundary: <c>git__*</c> is Thalos's <c>GitActionTools</c> (branch, commit, push, open pull
    ///     request), registered by Daedalus and bound to <see cref="DeveloperPolicy"/> exactly like
    ///     <c>repoaction__*</c> above. Same reasoning, different tool source.
    /// </summary>
    [Fact]
    public void The_git_tools_are_exposed_under_their_own_source()
    {
        ToolNames().Should().Contain("git__open_pull_request");
    }

    [Fact]
    public void Every_git_tool_is_bound_to_the_developer_policy()
    {
        var policies = ToolPolicyBindings();
        var gitTools = ToolNames().Where(n => n.StartsWith("git__", StringComparison.Ordinal)).ToList();

        gitTools.Should().NotBeEmpty("otherwise this test passes vacuously");

        foreach (var tool in gitTools)
        {
            policies.Any(b => GlobMatches(b.ToolPattern, tool) && string.Equals(b.PolicyName, DeveloperPolicy.PolicyName, StringComparison.Ordinal))
                .Should().BeTrue($"{tool} must be denied at the authorizer, not only by a tool list");
        }
    }

    [Fact]
    public async Task The_scheduled_run_principal_fails_the_developer_policy()
    {
        var scheduled = new DetachedPrincipal("schedule:daedalus", ["reader"]);

        var result = await new DeveloperPolicy().EvaluateAsync(scheduled);

        result.IsFailure.Should().BeTrue(
            "this is the layer that holds when someone widens a tool list by accident");
    }

    /// <summary>
    ///     The one configuration line the whole boundary rests on. Layer 3 holds only because a detached run
    ///     carries roles that <see cref="DeveloperPolicy"/> rejects, and those roles come from
    ///     <c>DetachedRuns:Roles</c> — so this reads the shipped value rather than restating it. Adding
    ///     <c>developer</c> or <c>admin</c> there would hand every unattended 07:00 run the ability to comment on,
    ///     label and close issues, and every other test in this file would stay green while it happened.
    /// </summary>
    [Fact]
    public async Task The_configured_detached_run_roles_fail_the_developer_policy()
    {
        var configured = _factory.Services.GetRequiredService<IOptions<DetachedRunOptions>>().Value;

        // Not defensive: empty roles here would mean the section bound nothing, and an empty role set fails the
        // policy for the wrong reason — the test would pass while proving nothing about the shipped configuration.
        configured.Roles.Should().NotBeEmpty("otherwise this test passes vacuously");

        var scheduled = new DetachedPrincipal(configured.PrincipalId, configured.Roles);

        var result = await new DeveloperPolicy().EvaluateAsync(scheduled);

        result.IsFailure.Should().BeTrue(
            $"DetachedRuns:Roles is [{string.Join(", ", configured.Roles)}] and must never grant " +
            $"'{DeveloperPolicy.DeveloperRole}' or '{DeveloperPolicy.AdminRole}' — that one line is what makes " +
            "the repoaction__* tool policy deny an unattended run at all");
    }

    /// <summary>Qualified <c>{source}__{tool}</c> names, read once from the built host in <see cref="InitializeAsync"/>.</summary>
    private IReadOnlyList<string> ToolNames() => _toolNames;

    /// <summary>The agent catalogue the host built from <c>Thalos:Agents</c>.</summary>
    private IReadOnlyList<AgentDefinition> AgentDefinitions() =>
        _factory.Services.GetRequiredService<IAgentCatalog>().Agents;

    /// <summary>
    ///     The tool-policy bindings <c>DefaultToolAuthorizer</c> is constructed from — the bound
    ///     <c>Thalos:ToolPolicies</c> array, not a copy of it.
    /// </summary>
    private IList<ToolPolicyBinding> ToolPolicyBindings() =>
        _factory.Services.GetRequiredService<IOptions<ThalosOptions>>().Value.ToolPolicies;

    /// <summary>
    ///     Thalos's own matcher, the one <c>ToolCatalog</c> applies to <see cref="AgentDefinition.Tools"/> and
    ///     <see cref="ToolPolicyBinding.Matches"/> applies to a qualified tool name. Never reimplement this.
    /// </summary>
    private static bool GlobMatches(string pattern, string qualifiedToolName) =>
        Glob.IsMatch(pattern, qualifiedToolName);

    /// <summary>
    ///     Reads the in-process tool sources registered on the host and qualifies every tool the way the runtime
    ///     does. MCP sources are skipped deliberately: enumerating one starts a real server process, and the
    ///     read/write split under test is entirely a property of the local sources.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadToolNamesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var names = new List<string>();

        foreach (var source in scope.ServiceProvider.GetServices<IToolSource>().OfType<LocalToolSource>())
        {
            var tools = await source.GetToolsAsync(CancellationToken.None);
            tools.IsSuccess.Should().BeTrue($"the '{source.Name}' tool source must enumerate");
            names.AddRange(tools.Value.Select(t => $"{source.Name}__{t.Name}"));
        }

        return names;
    }
}
