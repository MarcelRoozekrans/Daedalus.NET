using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Pins the shape <c>processes/manufacture.yaml</c> has at version 7: both manufacturing nodes on their
///     squad roles, <c>implement</c> carrying an outcome set it did not have at version 2, <c>review</c>
///     declaring the three lenses <see cref="ReviewLens"/> knows how to run, and — from phase 2.4 task B4 — a
///     <c>retrospect</c> node between <c>review</c> and the human gate, and - from phase 2.5 task B14 - a <c>publish</c>
///     node that is the <c>open-pull-request</c> host action rather than an agent turn. Reads the real, deployed file through the
///     same <see cref="ProcessLoader"/> production uses, and validates it through the same
///     <see cref="ProcessValidator"/> <c>ProcessDefinitionSync</c> runs before activating anything.
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

    /// <summary>
    ///     The header comment says the publish-side refusals of <c>.gitattributes</c> or <c>.gitmodules</c> at any depth,
    ///     symlinks and submodule pointers apply when a sandboxed run is published, never to publish in general, since
    ///     local mode commits the worktree directly. The yaml is only read as text here; the comment is the content
    ///     the version bump covers. Red: dropping "When a sandboxed run is published," from the comment turns the
    ///     first assertion red; deleting the "symlinks and git submodule pointers" clause the second; rewording the
    ///     comment to say "Publish also refuses" with no sandbox scoping turns the third red.
    /// </summary>
    [Fact]
    public void The_header_scopes_the_publish_side_refusals_to_a_sandboxed_run()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "processes", "manufacture.yaml");
        var comment = string.Join(' ', File.ReadAllLines(path)
            .Where(l => l.TrimStart().StartsWith('#'))
            .Select(l => l.TrimStart().TrimStart('#').Trim()));
        comment = string.Join(' ', comment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        comment.Should().Contain("When a sandboxed run is published, publish also refuses a `.gitattributes` or `.gitmodules` at any depth");
        comment.Should().Contain("and symlinks and git submodule pointers");
        comment.Should().NotContain("Publish also refuses");
    }

    [Fact]
    public void The_process_is_at_version_eight()
    {
        var definition = LoadManufactureProcess();

        definition.Name.Should().Be("manufacture");

        // Falsifiable: setting `version:` back to 7 in processes/manufacture.yaml turns this red. The number is
        // load-bearing rather than cosmetic - phase 2.2's content-hash immutability refuses a same-version
        // content change, so a v8 body still labelled v7 is not a cosmetic slip, it is a file the store will
        // refuse to activate while the older v7 keeps running. Version 7 (phase 2.6, task B7) rewrites constraint 1 and the two
        // skills for sandbox mode and leaves the graph unchanged. Version 8 (phase 2.6, ruling R61) states the run mode
        // to implement and review instead of leaving them to infer it from their tool lists, and leaves the graph unchanged.
        definition.Version.Should().Be(8);
    }

    /// <summary>
    ///     Phase 2.5 task B14: publishing is host code run after the human gate, not an agent turn. An action node
    ///     names no agent and no skill, so no model is dispatched there, and its two outcomes are the two
    ///     <see cref="OpenPullRequestAction"/> reports.
    /// </summary>
    [Fact]
    public void Publish_is_the_open_pull_request_action_with_published_and_failed()
    {
        var publish = LoadManufactureProcess().Nodes["publish"];

        // Falsifiable: keeping `agent:` or `skill:` on publish turns these red.
        publish.Agent.Should().BeNull("no agent turn runs at publish any more");
        publish.Skill.Should().BeNull("no skill is loaded at publish any more");
        publish.Action.Should().Be(OpenPullRequestAction.ActionName);
        publish.Outcomes.Should().BeEquivalentTo(["published", "failed"]);

        // Falsifiable: pointing `failed` at `done`, or dropping either edge, turns this red. A publish that failed
        // must never end the run as succeeded.
        publish.Branch.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["published"] = "done",
            ["failed"] = "adjudicate",
        });
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
        // agent today - implement, review and retrospect. Version 6 took publish off the list: it is a host action.
        // Falsifiable: putting `agent:` back on publish turns this red.
        named.Should().HaveCount(3, "implement, review and retrospect each run an agent, and publish runs none");

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
        // Version 5 (task B4): an approval no longer goes straight to the human gate - it goes to `retrospect`
        // first. Falsifiable: pointing `approved` back at `gate` turns this red.
        review.Branch!["approved"].Should().Be("retrospect");
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

    /// <summary>
    ///     The node task B4 added: it must be pinned to the exact skill name
    ///     <see cref="ReviewHandoff.RetrospectSkillName"/> keys its projection on, run as the reviewer (the role
    ///     that holds no write tool), declare no lenses (it is not a review pass), and branch both its outcomes
    ///     to the human gate, never around it.
    /// </summary>
    [Fact]
    public void Retrospect_runs_the_reviewer_declares_no_lenses_and_always_reaches_the_gate()
    {
        var retrospect = LoadManufactureProcess().Nodes["retrospect"];

        retrospect.Agent.Should().Be("reviewer");
        // Falsifiable: renaming this node's `skill:` turns this red, and turns
        // ProcessDefinitionDriftTests.The_shipped_retrospect_node_uses_the_skill_the_projection_keys_on red too -
        // ReviewHandoffWorkflowStore.ProjectionForAsync and StandingInstructionsRunner both key off this exact
        // string.
        retrospect.Skill.Should().Be(ReviewHandoff.RetrospectSkillName);
        retrospect.Outcomes.Should().BeEquivalentTo(["proposed", "none"]);

        // Falsifiable: declaring `lenses:` on this node turns this red - retrospect is not a review pass, and
        // ReviewHandoffWorkflowStore.ProjectionForAsync would misclassify it as one and project the wrong keys.
        retrospect.Lenses.Should().BeEmpty();

        // Falsifiable: pointing either outcome anywhere but `gate` turns this red - both a proposal and no
        // proposal require the same human approval before anything downstream of this run can act on either.
        retrospect.Branch.Should().NotBeNull();
        retrospect.Branch!["proposed"].Should().Be("gate");
        retrospect.Branch["none"].Should().Be("gate");
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
        skillNames.Should().Contain(["manufacture-implement", "manufacture-review", "manufacture-retrospect"]);

        // Version 6 pins manufacture-publish nowhere. The skill stays on disk because v5 runs pin it, but the active
        // graph must not reach it. Falsifiable: pinning publish back to the skill turns this red.
        definition.Nodes.Values.Select(n => n.Skill).Should().NotContain("manufacture-publish");

        // The host actions come from the composed host rather than a list restated here, so the action publish names
        // must be one the host registers. Falsifiable: a resolver built without actions answers false for every
        // name, and the validation below fails with the validator's own message.
        var actionNames = await RegisteredHostActionNamesAsync();
        actionNames.Should().NotBeEmpty("the engine-on Api host registers open-pull-request");

        var resolver = Substitute.For<IWorkflowReferenceResolver>();
        resolver.ResolveAgentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<AgentId?>(agentNames.Contains(ci.Arg<string>()) ? new AgentId(Guid.NewGuid()) : null));
        resolver.SkillExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<bool>(skillNames.Contains(ci.Arg<string>())));
        resolver.HostActionExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<bool>(actionNames.Contains(ci.Arg<string>())));

        var result = await ProcessValidator.ValidateAsync(definition, resolver, CancellationToken.None);

        // Falsifiable: pointing review's `rejected` branch at a node that does not exist, or removing
        // `onExceeded` while keeping `maxVisits`, turns this red with the validator's own message.
        result.IsSuccess.Should().BeTrue(result.IsFailure ? $"processes/manufacture.yaml must validate, or nothing can run it: {result.Error}" : "");
    }

    /// <summary>
    ///     The names of every <see cref="IWorkflowHostAction"/> the shipped Api host registers, composed through the
    ///     same internal <c>AddDaedalusAgents</c> seam <c>WorkflowWriteConfigTests</c> uses.
    /// </summary>
    private static async Task<HashSet<string>> RegisteredHostActionNamesAsync()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Daedalus.Api.appsettings.json", optional: false)
            .Build();
        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusAgents(options, configuration, environment);

        await using var sp = services.BuildServiceProvider();
        return sp.GetServices<IWorkflowHostAction>().Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
    }
}
