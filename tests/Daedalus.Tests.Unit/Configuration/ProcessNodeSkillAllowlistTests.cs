using Daedalus.Agents;
using Microsoft.Extensions.Configuration;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Guards the exact defect phase 2.2's Task 11 hit: the <c>publish</c> node in
///     <c>processes/manufacture.yaml</c> pointed at the <c>writer</c> agent, whose <c>Skills</c> allow-list was
///     <c>[]</c>, so the node could never load the skill it was pinned to. Reads the real, deployed
///     <c>processes/manufacture.yaml</c> through the same <see cref="ProcessLoader"/> production uses (not a
///     hand-rolled YAML read), and the real <c>Thalos:Agents</c> catalogue from
///     <c>Daedalus.Api/appsettings.json</c>, then checks every node's <c>agent</c>/<c>skill</c> pin against that
///     agent's <c>Skills</c> allow-list.
/// </summary>
/// <remarks>
///     Today every node in <c>processes/manufacture.yaml</c> names <c>Daedalus Architect</c>, whose
///     <c>Skills</c> is <c>["*"]</c>, so this passes trivially - that is the point. The moment a later task
///     rewires a node to <c>implementer</c> or <c>reviewer</c>, this goes red unless that agent's <c>Skills</c>
///     already grants the pinned skill, rather than the gap surfacing only when that node's turn produces no
///     usable output at runtime.
/// </remarks>
public sealed class ProcessNodeSkillAllowlistTests
{
    private static IConfiguration LoadApiConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Daedalus.Api.appsettings.json", optional: false)
            .Build();

    private static ProcessDefinition LoadManufactureProcess()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "processes", "manufacture.yaml");
        File.Exists(path).Should().BeTrue("processes/**/*.yaml must be a Content item in Daedalus.Api.csproj");

        var result = ProcessLoader.Load(File.ReadAllText(path));
        result.IsSuccess.Should().BeTrue("processes/manufacture.yaml must parse, or nothing downstream of it can be trusted");
        return result.Value;
    }

    [Fact]
    public void Every_node_agent_can_load_the_skill_the_process_pins_to_it()
    {
        var definition = LoadManufactureProcess();

        var agents = new DaedalusAgentsOptions();
        LoadApiConfiguration().GetSection(DaedalusAgentsOptions.SectionName).Bind(agents);
        var agentsByName = agents.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);

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
                $"processes/manufacture.yaml pins a node to agent '{agentName}', which must exist in Thalos:Agents");

            var canLoad = agentsByName[agentName].Skills.Contains("*", StringComparer.Ordinal) || agentsByName[agentName].Skills.Contains(skill, StringComparer.Ordinal);
            canLoad.Should().BeTrue(
                $"agent '{agentName}' must be able to load skill '{skill}' (Thalos:Agents:*:Skills) - otherwise " +
                "the node pinned to it cannot load its own instructions, exactly the defect phase 2.2's Task 11 " +
                "found for the publish node and the writer agent");
        }
    }
}
