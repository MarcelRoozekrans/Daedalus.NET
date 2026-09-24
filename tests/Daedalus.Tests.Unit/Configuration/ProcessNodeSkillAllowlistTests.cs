using Daedalus.Agents;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Thalos.Workflow;
using static Daedalus.Tests.Unit.Configuration.ChartersTestSupport;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Guards the exact defect phase 2.2's Task 11 hit: the <c>publish</c> node in
///     <c>processes/manufacture.yaml</c> pointed at the <c>writer</c> agent, whose <c>Skills</c> allow-list was
///     <c>[]</c>, so the node could never load the skill it was pinned to. Reads the real, deployed
///     <c>processes/manufacture.yaml</c> through the same <see cref="ProcessLoader"/> production uses (not a
///     hand-rolled YAML read), and the real, <em>composed</em> agent catalogue built from
///     <c>Daedalus.Api/appsettings.json</c> and the shipped <c>roles/</c> charters, then checks every node's
///     <c>agent</c>/<c>skill</c> pin against that agent's <c>Skills</c> allow-list.
/// </summary>
/// <remarks>
///     <b>This is no longer the trivial pass it was written as.</b> The original remark said every node named
///     <c>Daedalus Architect</c>, whose <c>Skills</c> is <c>["*"]</c>, and that the guard would start doing
///     work "the moment a later task rewires a node to <c>implementer</c> or <c>reviewer</c>". That moment was
///     commit <c>73ee27b</c>. <c>implement</c> names <c>implementer</c> with <c>Skills</c>
///     <c>["manufacture-implement"]</c> and <c>review</c> names <c>reviewer</c> with
///     <c>["manufacture-review", "manufacture-retrospect"]</c>, so two of the three pins are now checked
///     against a narrow allow-list rather than a wildcard, and dropping either skill from either agent's
///     charter turns this red.
///     <para>
///     <b>Phase 2.4 task B2.</b> <c>implementer</c>/<c>reviewer</c> are chartered agents now: their <c>Skills</c>
///     comes from <c>roles/&lt;name&gt;.md</c>, not from a <c>Skills</c> key on their <c>Thalos:Agents</c> entry
///     (which no longer has one). Reading <see cref="DaedalusAgentsOptions"/> directly, as this test used to,
///     would see an empty list for both and the guard would go red for every pin naming them - it now reads the
///     same composed <see cref="IAgentCatalog"/> a real host serves, built by actually syncing the shipped
///     charters, which is correct for every agent (chartered or not), not only the two that moved.
///     </para>
///     <para>
///     <c>SquadConfigurationDriftTests.The_fallback_agent_can_load_every_skill_the_manufacture_process_pins</c>
///     is the neighbouring guard for the other mode: this one checks each node against the agent the process
///     <em>names</em>, that one checks the single agent every node collapses onto when the squad is off.
///     </para>
/// </remarks>
public sealed class ProcessNodeSkillAllowlistTests
{
    private static ProcessDefinition LoadManufactureProcess()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "processes", "manufacture.yaml");
        File.Exists(path).Should().BeTrue("processes/**/*.yaml must be a Content item in Daedalus.Api.csproj");

        var result = ProcessLoader.Load(File.ReadAllText(path));
        result.IsSuccess.Should().BeTrue("processes/manufacture.yaml must parse, or nothing downstream of it can be trusted");
        return result.Value;
    }

    [Fact]
    public async Task Every_node_agent_can_load_the_skill_the_process_pins_to_it()
    {
        var definition = LoadManufactureProcess();

        await using var sp = await BuildWithApiConfigurationAndSyncedChartersAsync();
        var agentsByName = sp.GetRequiredService<IAgentCatalog>().Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);

        var pins = definition.Nodes.Values
            .Where(n => n.Agent is not null && n.Skill is not null)
            .Select(n => (Agent: n.Agent!, Skill: n.Skill!))
            .ToList();

        // Falsifiability guard against a vacuous pass: an assertion over an empty sequence would be trivially
        // true and would prove nothing. processes/manufacture.yaml pins a skill to an agent on three of its six
        // nodes today (implement, review, publish) - if that ever dropped to zero, this must fail loudly rather
        // than silently pass.
        pins.Should().NotBeEmpty("processes/manufacture.yaml must pin at least one node's skill to an agent");

        foreach (var (agentName, skill) in pins)
        {
            agentsByName.Should().ContainKey(agentName,
                $"processes/manufacture.yaml pins a node to agent '{agentName}', which must exist in the agent catalogue");

            var canLoad = agentsByName[agentName].Skills.Contains("*", StringComparer.Ordinal) || agentsByName[agentName].Skills.Contains(skill, StringComparer.Ordinal);
            canLoad.Should().BeTrue(
                $"agent '{agentName}' must be able to load skill '{skill}' - otherwise the node pinned to it " +
                "cannot load its own instructions, exactly the defect phase 2.2's Task 11 found for the publish " +
                "node and the writer agent");
        }
    }
}
