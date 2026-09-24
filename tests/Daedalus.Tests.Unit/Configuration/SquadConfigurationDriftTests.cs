using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Thalos.Workflow;
using static Daedalus.Tests.Unit.Configuration.ChartersTestSupport;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Guards the phase 2.3 manufacturing squad's roster and flag against the real, shipped configuration:
///     <c>Daedalus.Api/appsettings.json</c> (the manufacturing host) leaves <c>Thalos:Squad:Enabled</c> on,
///     <c>Daedalus.Cli/appsettings.json</c> (the interactive developer tool, same precedent as
///     <c>Thalos:Workflow:Enabled</c>) turns it off, the reviewer's tool list stays a positive read-only
///     enumeration, and both new agents are actually reachable through the real DI composition root rather than
///     only declared in JSON — the gap <c>Daedalus.Tests.Integration.Migrations.ProcessDefinitionSyncEndToEndTests</c>'
///     own history (Task 11, phase 2.2) found for <c>processes/</c>.
/// </summary>
public sealed class SquadConfigurationDriftTests
{
    private const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    private const string CliAppSettingsFileName = "Daedalus.Cli.appsettings.json";

    private static IConfiguration Load(string fileName) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(fileName, optional: false)
            .Build();

    /// <summary>
    ///     <c>implementer</c> and <c>reviewer</c> are chartered agents (phase 2.4 task B2): <see cref="IAgentCatalog"/>
    ///     serves only config agents until a role's charter has actually been synced, so every helper here that
    ///     expects to find either of them goes through <see cref="ChartersTestSupport.BuildWithApiConfigurationAndSyncedChartersAsync"/>
    ///     rather than a plain, unsynced composition.
    /// </summary>
    private static async Task<AgentId?> ResolveThroughRealCompositionAsync(bool squadEnabled, string roleName)
    {
        await using var sp = await BuildWithApiConfigurationAndSyncedChartersAsync(squadEnabled);
        var resolver = sp.GetRequiredService<IWorkflowReferenceResolver>();
        return await resolver.ResolveAgentIdAsync(roleName, CancellationToken.None);
    }

    private static async Task<AgentId> AgentNamedAsync(string name)
    {
        await using var sp = await BuildWithApiConfigurationAndSyncedChartersAsync();
        return sp.GetRequiredService<IAgentCatalog>().Agents.Single(a => string.Equals(a.Name, name, StringComparison.Ordinal)).Id;
    }

    /// <summary>
    ///     The half task B4 left undone and this configuration's comments used to promise: with the squad off,
    ///     the agent a process node's role actually dispatches as is the fallback, resolved through the real
    ///     <see cref="IWorkflowReferenceResolver"/> the composition root registers — not through a
    ///     hand-constructed <see cref="SquadAgentResolver"/>. <c>processes/manufacture.yaml</c> names
    ///     <c>implementer</c> and <c>reviewer</c> directly, so nothing else would roll it back.
    /// </summary>
    [Theory]
    [InlineData("implementer")]
    [InlineData("reviewer")]
    public async Task With_the_squad_off_the_real_resolver_maps_every_role_to_the_fallback_agent(string roleName)
    {
        var resolved = await ResolveThroughRealCompositionAsync(squadEnabled: false, roleName);

        resolved.Should().Be(await AgentNamedAsync("Daedalus Architect"),
            "a disabled squad must keep the pipeline runnable on the configuration phase 2.2 proved, without editing the process file");
        resolved.Should().NotBe(await AgentNamedAsync(roleName));
    }

    [Theory]
    [InlineData("implementer")]
    [InlineData("reviewer")]
    public async Task With_the_squad_on_the_real_resolver_maps_a_role_to_its_own_agent(string roleName)
    {
        var resolved = await ResolveThroughRealCompositionAsync(squadEnabled: true, roleName);

        resolved.Should().Be(await AgentNamedAsync(roleName),
            "otherwise the flag is stuck on the fallback and the squad never runs at all");
    }

    /// <summary>
    ///     The rollback is only real if the fallback agent can load <em>every</em> skill the process pins,
    ///     because with the squad off it runs every node. <see cref="ProcessNodeSkillAllowlistTests"/> checks
    ///     each node against the agent the process <em>names</em>; nothing checked the agent every node
    ///     collapses onto, so a role-only skill would have left <c>Thalos:Squad:Enabled=false</c> failing at the
    ///     node rather than rolling back.
    /// </summary>
    [Fact]
    public void The_fallback_agent_can_load_every_skill_the_manufacture_process_pins()
    {
        var options = new DaedalusAgentsOptions();
        Load(ApiAppSettingsFileName).GetSection(DaedalusAgentsOptions.SectionName).Bind(options);

        var path = Path.Combine(AppContext.BaseDirectory, "processes", "manufacture.yaml");
        var definition = ProcessLoader.Load(File.ReadAllText(path));
        definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : null);

        var pinnedSkills = definition.Value.Nodes.Values
            .Where(n => n.Agent is not null && n.Skill is not null)
            .Select(n => n.Skill!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        pinnedSkills.Should().NotBeEmpty("an assertion over no pinned skills would prove nothing");

        var fallback = options.Agents.Should().ContainSingle(a => a.Name == options.Squad.FallbackAgentName).Subject;
        foreach (var skill in pinnedSkills)
        {
            var canLoad = fallback.Skills.Contains("*", StringComparer.Ordinal)
                || fallback.Skills.Contains(skill, StringComparer.Ordinal);
            canLoad.Should().BeTrue(
                $"with the squad off, agent '{fallback.Name}' runs every node, so it must be able to load '{skill}'");
        }
    }

    /// <summary>
    ///     The value the rollback needs, pinned on both hosts. Only <c>Enabled</c> was pinned before, so the
    ///     natural rollback — deleting the whole <c>"Squad"</c> block rather than flipping one boolean — was
    ///     caught for one key and not the other, and a blank <c>FallbackAgentName</c> is worse than a wrong one:
    ///     Thalos' <c>WorkflowReferenceResolver.ResolveAgentIdAsync</c> opens with <c>ThrowIfNullOrWhiteSpace</c>,
    ///     so it throws rather than failing the run cleanly.
    /// </summary>
    [Theory]
    [InlineData(ApiAppSettingsFileName)]
    [InlineData(CliAppSettingsFileName)]
    public void Both_hosts_declare_a_fallback_agent_that_exists_in_their_own_roster(string fileName)
    {
        var configuration = Load(fileName);

        // The key existing is asserted separately from its value, for the same reason Enabled is below: the
        // code default is "", which is blank, so a value-only assertion would have to be "not blank" and would
        // then be satisfied by any typo. Falsifiable: deleting the key, blanking it, or pointing it at an agent
        // no longer in this host's Thalos:Agents each turn one of these red.
        configuration.GetSection("Thalos:Squad:FallbackAgentName").Exists().Should()
            .BeTrue($"{fileName} must declare Thalos:Squad:FallbackAgentName explicitly, not rely on the blank default");

        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);

        options.Squad.FallbackAgentName.Should().Be("Daedalus Architect",
            "every workflow role collapses onto this agent when the squad is off, and both hosts must agree on which");
        options.Agents.Should().Contain(a => a.Name == options.Squad.FallbackAgentName,
            $"{fileName} must declare the fallback agent it names, or a squad-off run resolves to nothing");
    }

    [Fact]
    public void Api_appsettings_declares_thalos_squad_enabled()
    {
        var configuration = Load(ApiAppSettingsFileName);

        // Asserting only the resolved value would pass just as well if the key were absent (SquadOptions.Enabled
        // defaults to false, so a missing key would actually read as disabled here) - but it would also pass if
        // some other default flipped. Assert the key is actually present before asserting what it says.
        configuration.GetSection("Thalos:Squad:Enabled").Exists().Should()
            .BeTrue($"{ApiAppSettingsFileName} must declare Thalos:Squad:Enabled explicitly");
        configuration.GetValue<bool>("Thalos:Squad:Enabled").Should()
            .BeTrue("the API is the host that runs manufacturing");
    }

    [Fact]
    public void Api_appsettings_declares_thalos_workflow_enabled_explicitly()
    {
        var configuration = Load(ApiAppSettingsFileName);

        // The key existing is the whole point, not its value. Daedalus.Tests.Integration references Api, Cli and
        // Console, each shipping a generically-named appsettings.json; MSBuild copies all three toward the test
        // output and only one survives. Daedalus.Cli's sets Thalos:Workflow:Enabled to false, so when its copy
        // wins that collision a host adding Daedalus.Api.appsettings.json afterwards has nothing to override
        // with - the engine silently stays off. Relying on the code default of true is what made that reachable.
        configuration.GetSection("Thalos:Workflow:Enabled").Exists().Should()
            .BeTrue($"{ApiAppSettingsFileName} must declare Thalos:Workflow:Enabled explicitly so a stray appsettings.json from another project cannot disable the engine");
        configuration.GetValue<bool>("Thalos:Workflow:Enabled").Should()
            .BeTrue("the API is the host that runs the workflow engine");
    }

    [Fact]
    public void Cli_appsettings_declares_thalos_squad_disabled()
    {
        var configuration = Load(CliAppSettingsFileName);

        // Both halves matter: SquadOptions.Enabled already defaults to false, so asserting only the value would
        // pass exactly as well with the key deleted entirely as with it present and false - the preceding phase
        // shipped exactly that gap for a different flag.
        configuration.GetSection("Thalos:Squad:Enabled").Exists().Should()
            .BeTrue($"{CliAppSettingsFileName} must declare Thalos:Squad:Enabled explicitly, not rely on the default");
        configuration.GetValue<bool>("Thalos:Squad:Enabled").Should().BeFalse();
    }

    [Fact]
    public void Reviewer_tool_list_holds_no_write_capable_roslyn_tool()
    {
        var options = new DaedalusAgentsOptions();
        Load(ApiAppSettingsFileName).GetSection(DaedalusAgentsOptions.SectionName).Bind(options);

        var reviewer = options.Agents.Should().ContainSingle(a => a.Name == "reviewer").Subject;

        // The wildcard check matters on its own: a reviewer configured with "roslyn__*" would still fail the two
        // prefix checks below to spot (StartsWith("roslyn__apply_") is false for the literal string "roslyn__*"),
        // so the tool-level isolation this phase exists to catch would slip through unnoticed without it.
        reviewer.Tools.Should().NotContain("roslyn__*",
            "a wildcard would silently admit any future write-capable roslyn tool");
        reviewer.Tools.Should().NotContain(t => t.StartsWith("roslyn__apply_", StringComparison.Ordinal));
        reviewer.Tools.Should().NotContain(t => t.StartsWith("roslyn__rename_", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The positive half of the enumeration: a reviewer that cannot search symbols or list solutions cannot
    ///     review code it did not write. Both are read-only tools in the real <c>RoslynCodeLens.Mcp</c> surface,
    ///     outside the four <c>find_*</c>/<c>get_*</c>/<c>analyze_*</c>/<c>go_to_definition</c> prefixes, so
    ///     admitting them by name does not weaken the isolation - that rests on <c>apply_code_action</c> being
    ///     absent, not on the list being short.
    /// </summary>
    [Fact]
    public void Reviewer_tool_list_includes_the_two_read_tools_outside_the_four_prefixes()
    {
        var options = new DaedalusAgentsOptions();
        Load(ApiAppSettingsFileName).GetSection(DaedalusAgentsOptions.SectionName).Bind(options);

        var reviewer = options.Agents.Should().ContainSingle(a => a.Name == "reviewer").Subject;

        reviewer.Tools.Should().Contain("roslyn__list_solutions").And.Contain("roslyn__search_symbols");
    }

    /// <summary>
    ///     The exact defect phase 2.2's Task 11 hit: the <c>writer</c> agent's <c>Skills</c> was <c>[]</c>, so
    ///     the <c>publish</c> node it was pinned to could never load its skill.
    ///     <see cref="ProcessNodeSkillAllowlistTests"/> covers this generically for every node in
    ///     <c>processes/manufacture.yaml</c>, and since commit <c>73ee27b</c> that file does point
    ///     <c>implement</c> at <c>implementer</c>, so the generic guard covers this pin too.
    /// </summary>
    /// <remarks>
    ///     Kept rather than deleted as redundant, and the reason is not belt-and-braces. The generic guard
    ///     reads the pin out of the process file, so it follows the file: a change that moved <c>implement</c>
    ///     onto some other agent would keep that guard green while this one goes red. This asserts the roster
    ///     entry itself, which is what a rollback or a later phase is most likely to disturb.
    ///     <para>
    ///     Phase 2.4 task B2 moved <c>Skills</c> off the config roster and onto the charter
    ///     (<c>roles/implementer.md</c>), so this now reads the composed catalog rather than
    ///     <c>DaedalusAgentsOptions</c> directly — the config entry itself carries no <c>Skills</c> any more.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Implementer_can_load_the_manufacture_implement_skill()
    {
        await using var sp = await BuildWithApiConfigurationAndSyncedChartersAsync();

        var implementer = sp.GetRequiredService<IAgentCatalog>().Agents.Should().ContainSingle(a => a.Name == "implementer").Subject;

        implementer.Skills.Should().Contain("manufacture-implement");
    }

    /// <remarks>
    ///     Phase 2.4 task B2 moved <c>Model</c> off the config roster and onto the charter
    ///     (<c>roles/reviewer.md</c>), so this now reads the composed catalog rather than
    ///     <c>DaedalusAgentsOptions</c> directly — the config entry itself carries no <c>Model</c> any more.
    /// </remarks>
    [Fact]
    public async Task Reviewer_is_priced_on_a_different_model_line_from_the_default()
    {
        var defaultModel = Load(ApiAppSettingsFileName)["Thalos:Anthropic:DefaultModel"];

        await using var sp = await BuildWithApiConfigurationAndSyncedChartersAsync();
        var reviewer = sp.GetRequiredService<IAgentCatalog>().Agents.Should().ContainSingle(a => a.Name == "reviewer").Subject;

        reviewer.Model.Should().NotBeNullOrWhiteSpace();
        reviewer.Model.Should().NotBe(defaultModel,
            "the reviewer must run a different model line from the implementer's inherited default, " +
            "or the two roles share every blind spot the split exists to avoid");
    }

    [Fact]
    public async Task Implementer_and_reviewer_resolve_from_real_configuration_and_appear_in_the_catalog()
    {
        await using var sp = await BuildWithApiConfigurationAndSyncedChartersAsync();

        var agents = sp.GetRequiredService<IAgentCatalog>().Agents;
        agents.Should().Contain(a => a.Name == "implementer");
        agents.Should().Contain(a => a.Name == "reviewer");

        // Declaring a roster in Thalos:Agents is not the same as it being reachable: this resolves both role
        // names through the real SquadAgentResolver, built over the real Thalos:Squad:Enabled=true from the
        // shipped Api configuration, not a hand-constructed SquadOptions.
        var resolver = sp.GetRequiredService<SquadAgentResolver>();
        resolver.Resolve("implementer").Should().Be("implementer");
        resolver.Resolve("reviewer").Should().Be("reviewer");
    }
}
