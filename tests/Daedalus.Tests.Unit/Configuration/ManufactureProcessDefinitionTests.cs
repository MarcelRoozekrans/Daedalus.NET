using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Configuration;
using Thalos;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Pins the shape phase 2.3 gave <c>processes/manufacture.yaml</c>: version 3, both manufacturing nodes on
///     their squad roles, <c>implement</c> carrying an outcome set it did not have at version 2, and
///     <c>review</c> declaring the three lenses <see cref="ReviewLens"/> knows how to run. Reads the real,
///     deployed file through the same <see cref="ProcessLoader"/> production uses, and validates it through the
///     same <see cref="ProcessValidator"/> <c>ProcessDefinitionSync</c> runs before activating anything.
/// </summary>
/// <remarks>
///     <see cref="ProcessNodeSkillAllowlistTests"/> is the neighbouring guard and covers a different question —
///     whether the agent a node is pinned to may load the skill it is pinned to. This one covers whether the
///     graph is the graph the design describes. Neither subsumes the other: a file could name agents that can
///     load their skills and still have lost its lens declaration, or the reverse.
/// </remarks>
public sealed class ManufactureProcessDefinitionTests
{
    private static ProcessDefinition LoadManufactureProcess()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "processes", "manufacture.yaml");
        File.Exists(path).Should().BeTrue("processes/**/*.yaml must be a Content item in Daedalus.Api.csproj");

        var result = ProcessLoader.Load(File.ReadAllText(path));
        result.IsSuccess.Should().BeTrue(result.IsFailure ? $"processes/manufacture.yaml must parse: {result.Error}" : "");
        return result.Value;
    }

    private static DaedalusAgentsOptions LoadAgents()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Daedalus.Api.appsettings.json", optional: false)
            .Build();

        var agents = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(agents);
        return agents;
    }

    [Fact]
    public void The_process_is_at_version_three()
    {
        var definition = LoadManufactureProcess();

        definition.Name.Should().Be("manufacture");

        // Falsifiable: setting `version:` back to 2 in processes/manufacture.yaml turns this red. The number is
        // load-bearing rather than cosmetic - phase 2.2's content-hash immutability refuses a same-version
        // content change, so a v3 body still labelled v2 is not a cosmetic slip, it is a file the store will
        // refuse to activate while an older v2 keeps running.
        definition.Version.Should().Be(3);
    }

    [Fact]
    public void Every_node_that_names_an_agent_names_one_that_exists()
    {
        var definition = LoadManufactureProcess();
        var agentNames = LoadAgents().Agents.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        var named = definition.Nodes
            .Where(n => n.Value.Agent is not null)
            .Select(n => (Node: n.Key, Agent: n.Value.Agent!))
            .ToList();

        // Guard against a vacuous pass: an assertion over an empty sequence proves nothing. Three nodes name an
        // agent today - implement, review and publish.
        named.Should().HaveCount(3, "implement, review and publish each run an agent");

        foreach (var (node, agent) in named)
        {
            agentNames.Should().Contain(agent,
                $"node '{node}' names agent '{agent}', which must exist in Thalos:Agents or the run fails at load");
        }
    }

    [Fact]
    public void Implement_runs_the_implementer_and_must_claim_a_change_to_reach_review()
    {
        var implement = LoadManufactureProcess().Nodes["implement"];

        implement.Agent.Should().Be("implementer");
        implement.Skill.Should().Be("manufacture-implement");

        // The heart of the version 3 change to this node. Version 2 declared no outcomes at all, so the node
        // completed on any turn output whatsoever - which was tolerable while it only wrote a description and is
        // not now it edits a working tree. Falsifiable: deleting the `outcomes:` block turns this red.
        implement.Outcomes.Should().NotBeNull();
        implement.Outcomes.Should().BeEquivalentTo(["changed", "blocked"],
            "\"I edited the tree\" must be an explicit claim, and there must be exactly one honest way out of the node that is not that claim");

        // `changed` is the ONLY edge into review: the reviewer is never dispatched except behind an assertion
        // that there is something on disk to read. Falsifiable: pointing `blocked` at review turns this red.
        implement.Branch.Should().NotBeNull();
        implement.Branch!["changed"].Should().Be("review");
        implement.Branch["blocked"].Should().Be("adjudicate");
        implement.Branch.Values.Where(v => string.Equals(v, "review", StringComparison.Ordinal))
            .Should().ContainSingle("only a claim of having changed something may reach the reviewer");
    }

    [Fact]
    public void Review_runs_the_reviewer_and_declares_the_three_lenses_in_order()
    {
        var review = LoadManufactureProcess().Nodes["review"];

        review.Agent.Should().Be("reviewer");
        review.Skill.Should().Be("manufacture-review");
        review.Outcomes.Should().BeEquivalentTo(["approved", "rejected"]);
        review.Branch!["approved"].Should().Be("gate");
        review.Branch["rejected"].Should().Be("implement");
        review.MaxVisits.Should().Be(5);
        review.OnExceeded.Should().Be("adjudicate");

        // Order matters and is asserted as a sequence, not a set: ReviewLensRunner runs the passes in declared
        // order and short-circuits on the first rejection, so which lens is cheapest to fail is a real choice.
        // Falsifiable: deleting `lenses:`, reordering it, or renaming a lens turns this red.
        review.Lenses.Should().NotBeNull();
        review.Lenses.Should().Equal("correctness", "falsifiability", "mechanism");

        // And the names must be ones the runner can actually resolve - a lens the code does not know is a node
        // that fails on every dispatch. Falsifiable: renaming a lens in either the YAML or ReviewLens.
        var resolved = ReviewLens.Resolve(review.Lenses);
        resolved.IsSuccess.Should().BeTrue(resolved.IsFailure ? $"every lens named in the process file must resolve: {resolved.Error}" : "");
        resolved.Value.Select(l => l.Name).Should().Equal("correctness", "falsifiability", "mechanism");
    }

    [Fact]
    public async Task The_graph_still_validates_against_the_real_agent_and_skill_catalogue()
    {
        var definition = LoadManufactureProcess();
        var options = LoadAgents();
        var agentNames = options.Agents.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var skillNames = Directory
            .EnumerateDirectories(Path.Combine(AppContext.BaseDirectory, "skills"))
            .Select(d => Path.GetFileName(d)!)
            .ToHashSet(StringComparer.Ordinal);

        // Guard against a vacuous pass: if either catalogue came back empty the resolver below would answer
        // "not found" for everything and this test would fail for the wrong reason - or, with a permissive
        // resolver, pass for the wrong reason. Assert both are populated before relying on them.
        agentNames.Should().NotBeEmpty();
        skillNames.Should().Contain(["manufacture-implement", "manufacture-review", "manufacture-publish"]);

        var resolver = Substitute.For<IWorkflowReferenceResolver>();
        resolver.ResolveAgentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<AgentId?>(agentNames.Contains(ci.Arg<string>()) ? new AgentId(Guid.NewGuid()) : null));
        resolver.SkillExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<bool>(skillNames.Contains(ci.Arg<string>())));

        var result = await ProcessValidator.ValidateAsync(definition, resolver, CancellationToken.None);

        // Falsifiable: pointing review's `rejected` branch at a node that does not exist, or removing
        // `onExceeded` while keeping `maxVisits`, turns this red with the validator's own message.
        result.IsSuccess.Should().BeTrue(result.IsFailure ? $"processes/manufacture.yaml must validate, or nothing can run it: {result.Error}" : "");
    }
}
