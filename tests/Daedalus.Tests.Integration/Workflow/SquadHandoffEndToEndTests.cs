using System.Data.Async.Adapters;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Daedalus.Agents;
using Daedalus.Agents.Memory;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Thalos;
using Thalos.Skills;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Drives a four-role manufacturing pipeline — an <c>implement</c> node that reports variables through its
///     outcome tool, a lens-running <c>review</c> node, and — since phase 2.4 task B4 — a <c>retrospect</c> node —
///     through the real <c>OrmWorkflowStore</c>, the real Thalos <see cref="WorkflowNodeDispatcher"/> and the
///     real <see cref="WorkflowNodeDispatcherFactory"/> composition, against a real Postgres database. Only
///     <see cref="ISubagentRunner"/> and the agent-name lookup are substituted; the store, the outbox, the
///     dispatcher, the variable plumbing and both store decorators are the shipped ones.
/// </summary>
/// <remarks>
///     <b>This suite exists because the assertion it makes could not fail before.</b> Task B4 proved "the
///     reviewer does not receive <c>summary</c> or <c>rationale</c>" against Thalos 0.8.0, where
///     <c>WorkflowNodeDispatcher</c> populated no variables at all — the bag was empty on every path, so the
///     claim held for a reason unrelated to the projection meant to enforce it. Thalos 0.9.0 both carries a
///     node's reported variables into <see cref="WorkflowRun.Variables"/> and renders the whole bag into the
///     next node's task text, which is the first time that assertion can fail for a real reason. Every test
///     here therefore asserts against the <em>task text the dispatcher actually built</em>, and
///     <see cref="The_run_record_holds_the_narrative_the_reviewer_was_not_shown"/> pins that the withheld
///     values really were in the run, so a green result cannot mean "nothing travelled".
///     <para>
///     <b>Phase 2.4 task B4: <see cref="RunAsync"/> now starts through <see cref="WorkflowRunStarter"/>, not
///     <c>store.StartAsync</c> directly.</b> Pinning is what makes <see cref="StandingInstructionsRunner"/>
///     exercisable at all — it reads <see cref="WorkflowRun.Manifest"/>, which only a pinned start ever
///     populates — so the harness now resolves every task node's agent and skill through a real
///     <see cref="CatalogRunManifestResolver"/> over a seeded <see cref="InMemorySkillStore"/> and a small
///     in-test <see cref="IAgentCatalog"/>, the same shape <c>WorkflowRunStarterTests</c> in Thalos.NET itself
///     uses.
///     </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class SquadHandoffEndToEndTests(PostgresFixture fixture)
{
    private const string ProcessName = "b5-handoff-test";

    private const string FilesTouched = "src/Daedalus.Infrastructure/Persistence/TaskRepository.cs";

    private const string SummarySentinel = "SUMMARY-MUST-NOT-REACH-THE-REVIEWER";

    private const string RationaleSentinel = "RATIONALE-MUST-NOT-REACH-THE-REVIEWER";

    private const string LearningsSentinel = "LEARNINGS-DOTNET-TEST-NEEDS-DOCKER";

    private const string StandingSentinel = "STANDING-INSTRUCTIONS-SENTINEL";

    private const string DefaultRetrospectProposal = "PROPOSED-STANDING-INSTRUCTIONS-SENTINEL";

    private const string WorkIntent = "Make ClaimNextAsync skip cancelled tasks";

    /// <summary>
    ///     An opening variable that is <em>outside</em> the review contract's read keys. Without one, every
    ///     assertion about the projection would be satisfied by a projection that does nothing on the implement
    ///     node: its bag holds only <c>work_intent</c>, which the review contract admits anyway. This key is
    ///     what makes "the implement node gets the whole bag" and "the review node gets an allow-list" two
    ///     different statements. Found by a falsifying edit that made every node a review node and turned no
    ///     test red.
    /// </summary>
    private const string OffContractSentinel = "OFF-CONTRACT-KEY-THE-REVIEWER-MUST-NOT-SEE";

    // Three distinct synthetic ids. They stand in for the real Thalos:Agents entries, which this suite has no
    // booted host to read - what the assertions need is only that the three are told apart, and that the id the
    // dispatcher used came back from the resolver rather than from the process file's name.
    private static readonly AgentId ImplementerId = new(new Guid(0x11111111, 0x2222, 0x4333, 0x84, 0x44, 0x55, 0x55, 0x66, 0x66, 0x77, 0x77));

    private static readonly AgentId ReviewerId = new(new Guid(0x22222222, 0x3333, 0x4444, 0x85, 0x55, 0x66, 0x66, 0x77, 0x77, 0x88, 0x88));

    private static readonly AgentId ArchitectId = new(new Guid(0x33333333, 0x4444, 0x4555, 0x86, 0x66, 0x77, 0x77, 0x88, 0x88, 0x99, 0x99));

    private const string Yaml = $"""
        process: {ProcessName}
        version: 1
        nodes:
          implement:
            agent: implementer
            skill: manufacture-implement
            outcomes: [changed, blocked]
            branch:
              changed: review
              blocked: stopped
          review:
            agent: reviewer
            skill: manufacture-review
            outcomes: [approved, rejected]
            branch:
              approved: retrospect
              rejected: implement
            lenses: [correctness, falsifiability, mechanism]
          retrospect:
            agent: reviewer
            skill: manufacture-retrospect
            outcomes: [proposed, none]
            branch:
              proposed: gate
              none: gate
          gate:
            await: human_approval
            next: done
          stopped:
            terminal: failed
          done:
            terminal: succeeded
        """;

    /// <summary>The implement node reports all three keys its skill declares, through the outcome tool.</summary>
    [Fact]
    public async Task The_reviewer_is_given_the_files_the_implementer_touched()
    {
        var result = await RunAsync(squadEnabled: true);

        result.TasksByNode["review"].Should().NotBeEmpty("the review node must have been dispatched at all");
        foreach (var task in result.TasksByNode["review"])
        {
            task.Should().Contain(FilesTouched,
                "files_touched is written by implement through the outcome tool's variables argument and is the " +
                "pointer the reviewer is supposed to start from; without it the review contract is decorative");
        }
    }

    /// <summary>
    ///     The assertion this whole suite is for. It is paired with
    ///     <see cref="The_run_record_holds_the_narrative_the_reviewer_was_not_shown"/>, which is what stops it
    ///     passing because nothing was written.
    /// </summary>
    [Fact]
    public async Task The_reviewer_is_never_given_the_implementers_summary_or_rationale()
    {
        var result = await RunAsync(squadEnabled: true);

        result.TasksByNode["review"].Should().NotBeEmpty();
        foreach (var task in result.TasksByNode["review"])
        {
            task.Should().NotContain(SummarySentinel,
                "a summary is rationale in compressed form; a reviewer handed it reviews the account, not the artifact");
            task.Should().NotContain(RationaleSentinel,
                "the reviewer must judge what is on disk, not the argument made for it");
        }
    }

    /// <summary>
    ///     Makes the assertion above non-vacuous: the withheld values reached the run's own record, so
    ///     "the reviewer did not see them" is a statement about the projection and not about an empty bag.
    /// </summary>
    [Fact]
    public async Task The_run_record_holds_the_narrative_the_reviewer_was_not_shown()
    {
        var result = await RunAsync(squadEnabled: true);

        result.FinalVariables.Should().ContainKey(ReviewHandoff.SummaryKey)
            .WhoseValue.Should().Be(SummarySentinel, "summary stays in the run record for humans and for adjudicate");
        result.FinalVariables.Should().ContainKey(ReviewHandoff.RationaleKey)
            .WhoseValue.Should().Be(RationaleSentinel);
        result.FinalVariables.Should().ContainKey(ReviewHandoff.FilesTouchedKey);
    }

    /// <summary>The implement node still receives everything: only a projected node is narrowed.</summary>
    [Fact]
    public async Task A_node_that_runs_no_lenses_is_given_the_whole_bag()
    {
        var result = await RunAsync(squadEnabled: true);

        result.TasksByNode["implement"].Should().NotBeEmpty();
        result.TasksByNode["implement"][0].Should().Contain(WorkIntent,
            "the opening variables reach the first node through Thalos' own rendering of the bag");
        result.TasksByNode["implement"][0].Should().Contain(OffContractSentinel,
            "a node with no read projection is not narrowed at all, including for keys the review contract does not name");
    }

    /// <summary>
    ///     The projection is an allow-list, not a deny-list of the two narrative keys. A key nobody has thought
    ///     of yet - one a later phase adds to the opening variables - must be excluded by default rather than
    ///     admitted silently.
    /// </summary>
    [Fact]
    public async Task The_reviewer_is_given_nothing_the_review_contract_does_not_name()
    {
        var result = await RunAsync(squadEnabled: true);

        result.TasksByNode["review"].Should().NotBeEmpty();
        foreach (var task in result.TasksByNode["review"])
        {
            task.Should().NotContain(OffContractSentinel,
                "a deny-list admits every field a later phase adds, and the leak is invisible until someone re-reads the filter");
        }
    }

    /// <summary>
    ///     Task B4: the retrospect node reads the implementer's <c>learnings</c> (through the retrospect
    ///     projection) and the run's pinned standing instructions (appended by
    ///     <see cref="StandingInstructionsRunner"/>), but never the implementer's narrative — the same isolation
    ///     the reviewer gets, extended to the one variable retrospect is allowed to read.
    /// </summary>
    [Fact]
    public async Task The_retrospect_node_sees_learnings_and_standing_instructions_but_not_the_narrative()
    {
        var result = await RunAsync(squadEnabled: true, documents: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManufactureRunStarter.StandingInstructionsDocument] = StandingSentinel,
        });

        var task = result.TasksByNode["retrospect"].Single();
        task.Should().Contain(LearningsSentinel).And.Contain(StandingSentinel);
        task.Should().NotContain(SummarySentinel).And.NotContain(RationaleSentinel);
    }

    /// <summary>
    ///     <see cref="StandingInstructionsRunner"/> is keyed on the pinned skill, not on the node name: only
    ///     <c>manufacture-implement</c> and <c>manufacture-retrospect</c> are eligible, so <c>review</c> — pinned
    ///     to <c>manufacture-review</c> — never receives the block, however many lens passes it runs.
    /// </summary>
    [Fact]
    public async Task The_implement_node_is_given_the_standing_instructions_and_the_review_node_is_not()
    {
        var result = await RunAsync(squadEnabled: true, documents: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManufactureRunStarter.StandingInstructionsDocument] = StandingSentinel,
        });

        result.TasksByNode["implement"].Should().OnlyContain(t => t.Contains(StandingSentinel));
        result.TasksByNode["review"].Should().OnlyContain(t => !t.Contains(StandingSentinel),
            "the reviewer judges the artifact, not the house rules the implementer followed");
    }

    /// <summary>
    ///     The design's amendment 3: rendering cuts a variable's value at 512 characters, but that is a
    ///     rendering concern, not a storage one — the run's real <c>Variables</c> bag keeps whatever the node
    ///     reported at full length, which is what a human resuming the gate needs <c>proposed_standing_instructions</c>
    ///     to still be.
    /// </summary>
    [Fact]
    public async Task A_run_reaches_the_gate_with_the_full_proposal_stored_uncut()
    {
        var proposal = new string('p', 3000);
        var result = await RunAsync(squadEnabled: true, retrospectProposal: proposal);

        result.FinalRun.Status.Should().Be(WorkflowStatus.Awaiting);
        ((string)result.FinalRun.Variables[ReviewHandoff.ProposedStandingInstructionsKey]!).Should().HaveLength(3000,
            "rendering cuts at 512, but the stored value is what the host writes to AGENT.md");
    }

    [Fact]
    public async Task With_the_squad_enabled_each_role_is_dispatched_as_itself()
    {
        var result = await RunAsync(squadEnabled: true);

        result.AgentsByNode["implement"].Should().Be(ImplementerId);
        result.AgentsByNode["review"].Should().Be(ReviewerId,
            "otherwise the flag is stuck on the fallback and the squad never runs");
    }

    [Fact]
    public async Task With_the_squad_disabled_every_node_is_dispatched_as_the_fallback_agent()
    {
        var result = await RunAsync(squadEnabled: false);

        result.AgentsByNode["implement"].Should().Be(ArchitectId);
        result.AgentsByNode["review"].Should().Be(ArchitectId,
            "disabling the squad must keep the pipeline runnable on the configuration phase 2.2 proved, " +
            "without editing the process file");
    }

    /// <summary>
    ///     The second, inseparable half of the squad-off behaviour: a <c>Succeeded</c> run in which one agent
    ///     implemented and reviewed its own work must not read like one with independent review.
    /// </summary>
    [Fact]
    public async Task A_squad_off_run_says_so_in_its_own_event_log()
    {
        var result = await RunAsync(squadEnabled: false);

        result.Status.Should().Be(WorkflowStatus.Awaiting, "the fallback must still reach the human gate");
        result.TransitionModes.Should().NotBeEmpty();
        result.TransitionModes.Should().OnlyContain(m => m != null && m.StartsWith(WorkflowRunModeStore.SquadDisabledPrefix, StringComparison.Ordinal),
            "every transition the run recorded must name the mode that produced it, in workflow_run_event and not only in a log");
        result.TransitionModes[0].Should().Contain("Daedalus Architect",
            "naming the agent is what makes the record readable six months later");
    }

    [Fact]
    public async Task A_squad_on_run_records_the_enabled_mode_instead()
    {
        var result = await RunAsync(squadEnabled: true);

        result.TransitionModes.Should().NotBeEmpty();
        result.TransitionModes.Should().OnlyContain(m => m == WorkflowRunModeStore.SquadEnabledValue,
            "the same channel has to carry both polarities, or a disabled run is distinguishable only by an absence");
    }

    /// <summary>
    ///     Final review finding C1, the attack end to end: <c>implement</c> reports
    ///     <c>proposed_standing_instructions</c> alongside its real variables, and <c>retrospect</c> then reports
    ///     <c>none</c> with no variables at all, exactly as its skill tells it to. Thalos leaves an unreported key
    ///     in place, so before the fix the planted text reached the gate. There, <c>GET</c> showed it as
    ///     retrospect's diff and an apply wrote it to disk.
    /// </summary>
    [Fact]
    public async Task A_proposal_planted_by_implement_never_reaches_the_gate()
    {
        var result = await RunAsync(squadEnabled: true, plantAt: "implement", retrospectOutcome: "none");

        await AssertNoProposalAtTheGateAsync(result);
    }

    /// <summary>
    ///     The same attack from <c>review</c>, which runs as the same agent as <c>retrospect</c> but not under its
    ///     skill. The planted key rides the last lens pass's outcome call, the turn ReviewLensRunner returns.
    /// </summary>
    [Fact]
    public async Task A_proposal_planted_by_review_never_reaches_the_gate()
    {
        var result = await RunAsync(squadEnabled: true, plantAt: "review", retrospectOutcome: "none");

        await AssertNoProposalAtTheGateAsync(result);
    }

    private static async Task AssertNoProposalAtTheGateAsync(RunOutcome result)
    {
        result.PlantsReported.Should().Be(1, "otherwise the attack was never attempted and this test proves nothing");
        result.FinalRun.Status.Should().Be(WorkflowStatus.Awaiting, "the run must be parked at the gate, where a human would look");
        result.FinalRun.CurrentNode.Should().Be("gate");

        if (result.FinalRun.Variables.TryGetValue(ReviewHandoff.ProposedStandingInstructionsKey, out var value))
        {
            value.Should().BeNull("only retrospect may author a proposal, and it proposed none");
        }

        StandingInstructionsWriter.Diff(result.FinalRun).Should().BeNull(
            "GET /api/workflow-runs/{id} must show no diff for a run whose retrospect proposed nothing");

        using var dir = new TempDirectory();
        var writer = new StandingInstructionsWriter(
            new WorkflowConfig { StandingInstructionsPath = dir.Path("AGENT.md") }, Substitute.For<IHostEnvironment>());
        var applied = await writer.ApplyAsync(result.FinalRun, CancellationToken.None);

        applied.IsFailure.Should().BeTrue("an apply must have nothing to write");
        applied.Error.Kind.Should().Be(ResumeRefusal.NoProposal);
        File.Exists(dir.Path("AGENT.md")).Should().BeFalse();
    }

    private sealed record RunOutcome(
        WorkflowRun FinalRun,
        IReadOnlyDictionary<string, IReadOnlyList<string>> TasksByNode,
        IReadOnlyDictionary<string, AgentId> AgentsByNode,
        IReadOnlyDictionary<string, string> FinalVariables,
        IReadOnlyList<string?> TransitionModes,
        int PlantsReported)
    {
        public WorkflowStatus Status => FinalRun.Status;
    }

    private async Task<RunOutcome> RunAsync(
        bool squadEnabled,
        IReadOnlyDictionary<string, string>? documents = null,
        string? retrospectProposal = null,
        string? plantAt = null,
        string retrospectOutcome = ReviewHandoff.RetrospectProposedOutcome)
    {
        var dbName = $"b5_handoff_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;

        try
        {
            await MigrateAsync(connectionString);

            var options = new WorkflowOrmOptions { ConnectionString = connectionString };
            var definitions = new OrmProcessDefinitionStore(options);
            var store = new OrmWorkflowStore(options, definitions);

            var definition = ProcessLoader.Load(Yaml);
            definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : null);
            (await definitions.UpsertAndActivateAsync(definition.Value, Yaml, CancellationToken.None)).IsSuccess.Should().BeTrue();

            var skills = new InMemorySkillStore(TimeProvider.System);
            foreach (var skillName in new[] { "manufacture-implement", "manufacture-review", "manufacture-retrospect" })
            {
                (await skills.UpsertAsync(SeedSkill(skillName), CancellationToken.None)).IsSuccess.Should().BeTrue();
            }

            var runner = new ScriptedRunner(retrospectProposal ?? DefaultRetrospectProposal, plantAt, retrospectOutcome);
            var (provider, references, catalog) = BuildProvider(store, definitions, runner, squadEnabled, skills);
            await using var disposable = provider;

            var nodeDispatcher = WorkflowNodeDispatcherFactory.Create(provider);
            var outboxDispatcher = new WorkflowDispatchOutboxDispatcher(nodeDispatcher);

            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            var poller = new WorkflowOutboxDispatchService(
                dataSource, outboxDispatcher, new WorkflowOutboxDispatchOptions(), NullLogger<WorkflowOutboxDispatchService>.Instance);

            var manifestResolver = new CatalogRunManifestResolver(references, catalog, skills);
            var starter = new WorkflowRunStarter(definitions, manifestResolver, store);

            var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ReviewHandoff.WorkIntentKey] = WorkIntent,
                ["issue_url"] = OffContractSentinel,
            };

            var started = await starter.StartAsync(ProcessName, $"b5-handoff:{Guid.NewGuid()}", variables, documents, CancellationToken.None);
            started.IsSuccess.Should().BeTrue(started.IsFailure ? started.Error : "");
            var runId = started.Value;

            // implement -> review -> retrospect -> gate is four dispatches; spare ticks so a gate that enqueued
            // nothing simply finds an empty batch rather than leaving the run mid-flight.
            for (var tick = 0; tick < 8; tick++)
            {
                await poller.ProcessBatchAsync(CancellationToken.None);
            }

            var run = await store.FindAsync(runId, CancellationToken.None);
            run.Should().NotBeNull();

            return new RunOutcome(
                run!,
                runner.TasksByNode,
                runner.AgentsByNode,
                run.Variables.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.Ordinal),
                await ReadTransitionModesAsync(connectionString, runId),
                runner.PlantsReported);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
        }
    }

    /// <summary>A minimal, active skill document for <paramref name="name"/> — the body's content is never read by this suite.</summary>
    private static SkillDocument SeedSkill(string name) => new()
    {
        Name = SkillName.Parse(name),
        Description = "a test skill",
        Body = "do the thing",
        SourcePath = $"{name}/SKILL.md",
        ContentHash = name,
        UpdatedAt = TimeProvider.System.GetUtcNow(),
    };

    /// <summary>
    ///     Builds the container <see cref="WorkflowNodeDispatcherFactory.Create"/> reads, with the same
    ///     <see cref="SquadWorkflowReferenceResolver"/> wrapping production DI applies. The inner name lookup is
    ///     substituted because a real <c>IAgentCatalog</c> would need a booted host;
    ///     <c>SquadConfigurationDriftTests</c> covers that the real composition wraps it the same way. Returns
    ///     the resolver and catalog alongside the provider so <see cref="RunAsync"/> can build a
    ///     <see cref="CatalogRunManifestResolver"/> from the exact same instances the dispatcher resolves out of
    ///     DI, rather than a second, possibly-diverging pair.
    /// </summary>
    private static (ServiceProvider Provider, IWorkflowReferenceResolver References, IAgentCatalog Catalog) BuildProvider(
        IWorkflowStore store, IProcessDefinitionStore definitions, ISubagentRunner runner, bool squadEnabled, ISkillStore skills)
    {
        var names = Substitute.For<IWorkflowReferenceResolver>();
        names.ResolveAgentIdAsync("implementer", Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentId?>(ImplementerId));
        names.ResolveAgentIdAsync("reviewer", Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentId?>(ReviewerId));
        names.ResolveAgentIdAsync("Daedalus Architect", Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentId?>(ArchitectId));

        var squad = new SquadOptions { Enabled = squadEnabled, FallbackAgentName = "Daedalus Architect" };
        var squadAgentResolver = new SquadAgentResolver(squad);
        var references = new SquadWorkflowReferenceResolver(names, squadAgentResolver, NullLogger<SquadWorkflowReferenceResolver>.Instance);
        var catalog = new FakeAgentCatalog(
        [
            Def("implementer", ImplementerId),
            Def("reviewer", ReviewerId),
            Def("Daedalus Architect", ArchitectId),
        ]);

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(definitions);
        services.AddSingleton(squad);
        services.AddSingleton(squadAgentResolver);
        services.AddSingleton<WorkflowRecallTierLog>();
        services.AddSingleton<IWorkflowReferenceResolver>(references);
        services.AddSingleton<IAgentCatalog>(catalog);
        services.AddSingleton(runner);
        services.AddSingleton(skills);
        services.AddSingleton(Options.Create(new DetachedRunOptions
        {
            PrincipalId = "workflow-test",
            Roles = ["workflow"],
            MaxTotalTokens = 42_000,
            DeadlineSeconds = 180,
        }));
        return (services.BuildServiceProvider(), references, catalog);
    }

    private static AgentDefinition Def(string name, AgentId id) => new()
    {
        Id = id,
        Name = name,
        Instructions = "do the thing",
    };

    /// <summary>
    ///     An <see cref="IAgentCatalog"/> over a fixed list, the same shape Thalos.NET's own
    ///     <c>WorkflowRunStarterTests</c> uses - this suite has no booted host to read a real one from.
    /// </summary>
    private sealed class FakeAgentCatalog(IReadOnlyList<AgentDefinition> agents) : IAgentCatalog
    {
        public IReadOnlyList<AgentDefinition> Agents { get; } = agents;

        public bool TryGet(AgentId id, [MaybeNullWhen(false)] out AgentDefinition definition)
        {
            definition = Agents.FirstOrDefault(a => a.Id == id);
            return definition is not null;
        }
    }

    /// <summary>
    ///     Reads <c>squad_mode</c> out of every <c>workflow_run_event</c> row that carries a transition's own
    ///     variables — the run record itself, not a log line, which is what design section 7 asks for.
    /// </summary>
    private static async Task<IReadOnlyList<string?>> ReadTransitionModesAsync(string connectionString, Guid runId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT variables FROM workflow_run_event WHERE run_id = @runId AND variables IS NOT NULL AND kind <> 'Entered' ORDER BY seq", connection);
        command.Parameters.AddWithValue("runId", runId);

        var modes = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            using var document = JsonDocument.Parse(reader.GetString(0));
            modes.Add(document.RootElement.TryGetProperty(WorkflowRunModeStore.SquadModeKey, out var mode) ? mode.GetString() : null);
        }

        return modes;
    }

    /// <summary>
    ///     Answers each node's turn the way the shipped skills instruct: <c>implement</c> reports its declared
    ///     variables (including the optional <c>learnings</c>) on the outcome call, each <c>review</c> lens pass
    ///     reports evidence through <c>daedalus__report_review_outcome</c> and the matching verdict through the
    ///     engine's own tool, and <c>retrospect</c> reports <c>proposed</c> with a replacement standing-instructions
    ///     text. With <c>plantAt</c> set, that node also reports <c>proposed_standing_instructions</c>, the C1
    ///     attack; with <c>retrospectOutcome</c> set to <c>none</c>, retrospect reports no variables at all.
    /// </summary>
    private sealed class ScriptedRunner(string retrospectProposal, string? plantAt, string retrospectOutcome) : ISubagentRunner
    {
        private const string PlantedProposal = "PLANTED-BY-A-NODE-THAT-IS-NOT-RETROSPECT";

        private readonly List<(string Node, string Task)> _turns = [];
        private readonly Dictionary<string, AgentId> _agents = new(StringComparer.Ordinal);
        private int _lensPass;

        public IReadOnlyDictionary<string, AgentId> AgentsByNode => _agents;

        public int PlantsReported { get; private set; }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> TasksByNode =>
            _turns
                .GroupBy(t => t.Node, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, IReadOnlyList<string> (g) => g.Select(t => t.Task).ToList(), StringComparer.Ordinal);

        public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
        {
            var run = ((WorkflowCaller)request.Caller).Run;
            _turns.Add((run.CurrentNode, request.Task));
            _agents[run.CurrentNode] = request.AgentId;

            var outcomeToolName = request.RequiredOutcome!.ToolName;
            var turn = run.CurrentNode switch
            {
                "implement" => ImplementTurn(outcomeToolName, Plant("implement")),
                "review" => ReviewTurn(outcomeToolName, _lensPass, Plant("review", onlyWhen: _lensPass++ == 2)),
                "retrospect" => RetrospectTurn(outcomeToolName, retrospectProposal, retrospectOutcome),
                _ => throw new InvalidOperationException($"ScriptedRunner has no script for node '{run.CurrentNode}'."),
            };

            return ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Success(turn));
        }

        /// <summary>
        ///     Whether <paramref name="node"/>'s report plants the key this turn. Counted, so a test can prove the
        ///     attack was attempted. <paramref name="onlyWhen"/> narrows review to its last lens pass, the one
        ///     whose turn the lens runner returns.
        /// </summary>
        private bool Plant(string node, bool onlyWhen = true)
        {
            if (!onlyWhen || !string.Equals(plantAt, node, StringComparison.Ordinal))
            {
                return false;
            }

            PlantsReported++;
            return true;
        }

        private static AgentTurnResult ImplementTurn(string outcomeToolName, bool plant)
        {
            var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ReviewHandoff.SummaryKey] = SummarySentinel,
                [ReviewHandoff.FilesTouchedKey] = FilesTouched,
                [ReviewHandoff.RationaleKey] = RationaleSentinel,
                [ReviewHandoff.LearningsKey] = new[] { LearningsSentinel },
            };
            if (plant)
            {
                variables[ReviewHandoff.ProposedStandingInstructionsKey] = PlantedProposal;
            }

            return new AgentTurnResult(
                TurnId.New(), new SessionId(Guid.Empty), "applied one code action", default,
                [
                    new ToolCallSummary(
                        ToolCallId.New(),
                        outcomeToolName,
                        JsonSerializer.Serialize(new { outcome = "changed", variables }),
                        Succeeded: true, "ok", TimeSpan.FromMilliseconds(2)),
                ],
                TimeSpan.FromSeconds(1));
        }

        private static AgentTurnResult ReviewTurn(string outcomeToolName, int pass, bool plant)
        {
            var outcomeArguments = plant
                ? JsonSerializer.Serialize(new
                {
                    outcome = "approved",
                    variables = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ReviewHandoff.ProposedStandingInstructionsKey] = PlantedProposal,
                    },
                })
                : $$"""{"{{OutcomeToolSchema.ArgumentName}}":"approved"}""";

            var lens = pass switch { 0 => "correctness", 1 => "falsifiability", _ => "mechanism" };
            return new AgentTurnResult(
                TurnId.New(), new SessionId(Guid.Empty), $"{lens}: approved", default,
                [
                    new ToolCallSummary(
                        ToolCallId.New(),
                        DaedalusReviewTools.QualifiedReportReviewOutcomeToolName,
                        JsonSerializer.Serialize(new
                        {
                            lens,
                            verdict = "approved",
                            @checked = """["TaskRepository.ClaimNextAsync now filters cancelled rows"]""",
                        }),
                        Succeeded: true, "Recorded", TimeSpan.FromMilliseconds(3)),
                    new ToolCallSummary(
                        ToolCallId.New(),
                        outcomeToolName,
                        outcomeArguments,
                        Succeeded: true, "ok", TimeSpan.FromMilliseconds(1)),
                ],
                TimeSpan.FromSeconds(2));
        }

        private static AgentTurnResult RetrospectTurn(string outcomeToolName, string proposal, string outcome)
        {
            // "none" passes no variables at all, exactly as skills/manufacture-retrospect/SKILL.md instructs.
            var arguments = string.Equals(outcome, ReviewHandoff.RetrospectProposedOutcome, StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new
                {
                    outcome,
                    variables = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ReviewHandoff.ProposedStandingInstructionsKey] = proposal,
                    },
                })
                : $$"""{"{{OutcomeToolSchema.ArgumentName}}":"{{outcome}}"}""";

            return new AgentTurnResult(
                TurnId.New(), new SessionId(Guid.Empty), "retrospect reported", default,
                [
                    new ToolCallSummary(ToolCallId.New(), outcomeToolName, arguments, Succeeded: true, "ok", TimeSpan.FromMilliseconds(2)),
                ],
                TimeSpan.FromSeconds(1));
        }
    }

    private static async Task MigrateAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var asyncConnection = connection.AsAsync();
        var dialect = new PostgresMigrationDialect();

        await new MigrationRunner(asyncConnection, OutboxOrmMigrations.Postgres, dialect).RunAsync();
        await new MigrationRunner(asyncConnection, WorkflowOrmMigrations.Postgres, dialect).RunAsync();
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
