using System.Text.Json;
using AwesomeAssertions.Execution;
using Daedalus.Agents;
using Daedalus.Agents.Decisions;
using Daedalus.Agents.Workflow;
using Daedalus.Tests.Unit.Decisions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Minos;
using Npgsql;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Results;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.10 task B4: <see cref="NodeSelectionRunner"/> decides an implement or review node's model and tool families
///     once per node visit, records the decision, reuses the record on a redelivery, and sets the request's
///     <see cref="SubagentRunRequest.Model"/> and <see cref="SubagentRunRequest.ToolFilter"/>.
/// </summary>
public sealed class NodeSelectionRunnerTests
{
    private static readonly AgentId ImplementerId = new(new Guid(0x3c_1d_77_a0, 0x5b2e, 0x4f61, 0x9a, 0x0b, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66));
    private static readonly AgentId ReviewerId = new(new Guid(0x4d_2e_88_b1, 0x6c3f, 0x4072, 0xab, 0x1c, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77));

    private static readonly string[] Tools = ["workspace__*", "daedalus__*", "roslyn__*", "sandbox__*"];

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    /// <summary>Two roles, each with a two-model menu and the same four tool families.</summary>
    private static DaedalusAgentsOptions Options()
    {
        var options = new DaedalusAgentsOptions();
        foreach (var (name, id) in new[] { ("implementer", ImplementerId), ("reviewer", ReviewerId) })
        {
            var agent = new AgentConfig { Id = id.ToString(), Name = name, Chartered = true };
            agent.AllowedModels.Add("cheap");
            agent.AllowedModels.Add("strong");
            foreach (var tool in Tools)
            {
                agent.Tools.Add(tool);
            }

            options.Agents.Add(agent);
        }

        return options;
    }

    /// <summary>A run at <paramref name="node"/>, pinned to <paramref name="skill"/> as <paramref name="agent"/>, on its given visits.</summary>
    private static WorkflowRun RunAt(
        string node, string skill, string agent = "implementer", Dictionary<string, int>? visits = null, bool manifest = true) => new()
        {
            Id = Guid.NewGuid(),
            Process = "manufacture",
            ProcessVersion = 11,
            CurrentNode = node,
            CurrentSeq = 7,
            Status = WorkflowStatus.Running,
            StartedBy = new RunPrincipal("u-starter", ["developer"]),
            Visits = visits ?? new Dictionary<string, int>(StringComparer.Ordinal) { [node] = 1 },
            Variables = new Dictionary<string, object?>(StringComparer.Ordinal) { [ReviewHandoff.WorkIntentKey] = "Make ClaimNextAsync skip cancelled tasks" },
            Manifest = manifest
            ? new RunManifest
            {
                Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal)
                {
                    [node] = new NodePin(agent, IdOf(agent), null, skill, "hash"),
                },
                Documents = new Dictionary<string, string>(StringComparer.Ordinal),
            }
            : null,
        };

    /// <summary>The reviewer's id for the reviewer, the implementer's for any other name.</summary>
    private static AgentId IdOf(string agent) => string.Equals(agent, "reviewer", StringComparison.Ordinal) ? ReviewerId : ImplementerId;

    private static SubagentRunRequest RequestFor(WorkflowRun run) => new()
    {
        AgentId = ImplementerId,
        Task = "the engine's task",
        Caller = new WorkflowCaller(run, grant: null),
    };

    /// <summary>Hard, confidently; roslyn not needed, everything else needed.</summary>
    private static FakeDecisionClient HardWithoutRoslyn() => FakeDecisionClient.Answering(Demand.Hard, 0.9, 0.1, 0.9, 0.9, 0.9, 0.9, 0.9);

    /// <summary>A record store that keeps what is appended and lists it back, like the real one.</summary>
    private sealed class InMemoryRecords : IWorkflowRunRecordStore
    {
        public List<WorkflowRunRecord> Appended { get; } = [];

        public List<WorkflowRunRecord> Seeded { get; } = [];

        public ValueTask AppendAsync(WorkflowRunRecord record, CancellationToken ct)
        {
            Appended.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<WorkflowRunRecord>> ListAsync(Guid runId, string? kind, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<WorkflowRunRecord>>(
                [.. Seeded.Concat(Appended).Where(r => r.RunId == runId && (kind is null || string.Equals(r.Kind, kind, StringComparison.Ordinal)))]);
    }

    /// <summary>Captures every request the runner forwards, and answers each with a plain turn.</summary>
    private sealed class CapturingRunner : ISubagentRunner
    {
        public List<SubagentRunRequest> Requests { get; } = [];

        public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Success(
                new AgentTurnResult(TurnId.New(), new SessionId(Guid.Empty), "done", default, [], TimeSpan.Zero)));
        }
    }

    /// <summary>
    ///     The scope the runner resolves its store, the agent catalog and the provider from. The implementer's charter
    ///     names no model, so its default is the provider's; the reviewer's names <c>charter-model</c>.
    /// </summary>
    private static IServiceScopeFactory Scopes(IWorkflowRunRecordStore records) =>
        RecordStoreScopes.For(records, services =>
        {
            var catalog = Substitute.For<IAgentCatalog>();
            catalog.TryGet(ImplementerId, Arg.Any<string?>(), out Arg.Any<AgentDefinition?>()).Returns(call =>
            {
                call[2] = new AgentDefinition { Id = ImplementerId, Name = "implementer", Instructions = "x" };
                return true;
            });
            catalog.TryGet(ReviewerId, Arg.Any<string?>(), out Arg.Any<AgentDefinition?>()).Returns(call =>
            {
                call[2] = new AgentDefinition { Id = ReviewerId, Name = "reviewer", Instructions = "x", Model = "charter-model" };
                return true;
            });
            services.AddSingleton(catalog);

            var provider = Substitute.For<IChatClientProvider>();
            provider.DefaultModel.Returns("provider-default");
            services.AddSingleton(provider);
        });

    private static NodeSelectionRunner Runner(ISubagentRunner inner, IDecisionClient? client, IWorkflowRunRecordStore records) =>
        new(
            inner,
            new NodeSelectionDecider(client, new DecisionsConfig(), NullLogger<NodeSelectionDecider>.Instance),
            Scopes(records),
            new FakeTimeProvider(Now),
            Options(),
            NullLogger<NodeSelectionRunner>.Instance);

    /// <summary>A recorded decision at <paramref name="seq"/> of <paramref name="run"/>, as an earlier attempt wrote it.</summary>
    private static WorkflowRun Recorded(InMemoryRecords records, WorkflowRun run, long seq, string model, string[]? toolFilter)
    {
        var payload = JsonSerializer.Serialize(new
        {
            level = "Routine",
            model,
            tool_filter = toolFilter,
            kept_families = toolFilter?.Length ?? 4,
            fallback = (string?)null,
            confidence = 0.8,
        });
        records.Seeded.Add(WorkflowRunRecord.Create(
            run.Id, seq, run.CurrentNode, WorkflowRunRecord.NodeSelectionKind, "earlier", null, payload, Now.UtcDateTime).Value);
        return run;
    }

    /// <summary>
    ///     A taken decision reaches the turn and its record. Reds, each applied once:
    ///     <list type="bullet">
    ///         <item>the model - Red: forward the request with <c>Model = null</c>;</item>
    ///         <item>the filter - Red: forward it with <c>ToolFilter = null</c>;</item>
    ///         <item>the record's key and authorship - Red: record at <c>CurrentSeq + 1</c>, under the review-evidence kind,
    ///             under a principal id other than the caller's, with no starter, or stamped a second off the clock;</item>
    ///         <item>the payload - Red: rename any one of the six JSON property names in the runner's payload record.</item>
    ///     </list>
    ///     The task text and the record's node are carried as they came; no change in this runner touches them, so they
    ///     are context for the other assertions rather than claims of their own.
    /// </summary>
    [Fact]
    public async Task The_decision_sets_the_requests_model_and_tool_filter()
    {
        var inner = new CapturingRunner();
        var records = new InMemoryRecords();
        using var client = HardWithoutRoslyn();
        var run = RunAt("implement", ReviewHandoff.ImplementSkillName);
        var request = RequestFor(run);

        var result = await Runner(inner, client, records).RunAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        var sent = inner.Requests.Should().ContainSingle().Subject;
        var record = records.Appended.Should().ContainSingle().Subject;
        using var payload = JsonDocument.Parse(record.PayloadJson);
        var root = payload.RootElement;
        using (new AssertionScope())
        {
            sent.Model.Should().Be("strong");
            sent.ToolFilter.Should().Equal("workspace__*", "daedalus__*", "sandbox__*");
            sent.Task.Should().Be("the engine's task");

            record.RunId.Should().Be(run.Id);
            record.Seq.Should().Be(7);
            record.Node.Should().Be("implement");
            record.Kind.Should().Be(WorkflowRunRecord.NodeSelectionKind);
            record.PrincipalId.Should().Be(request.Caller.Id);
            record.StartedById.Should().Be("u-starter");
            record.CreatedAt.Should().Be(Now.UtcDateTime);

            root.GetProperty("level").GetString().Should().Be("Hard");
            root.GetProperty("model").GetString().Should().Be("strong");
            root.GetProperty("tool_filter").EnumerateArray().Select(e => e.GetString()).Should().Equal("workspace__*", "daedalus__*", "sandbox__*");
            root.GetProperty("kept_families").GetInt32().Should().Be(3);
            root.GetProperty("fallback").ValueKind.Should().Be(JsonValueKind.Null);
            root.GetProperty("confidence").GetDouble().Should().Be(0.9);
        }
    }

    /// <summary>
    ///     A fallback leaves the turn exactly as it was, today's behaviour, and records the default it stands for: the
    ///     charter's model, or the provider's when the charter names none. Reds, each applied once: set <c>Model</c> on a
    ///     fallback too, and the null-model assertion fails; take the default from the role's <c>AgentConfig.Model</c>
    ///     instead of the agent definition, and the reviewer row records the provider's model; replace the provider
    ///     fallback with a constant, and the implementer row records that. The null filter and the recorded fallback and
    ///     confidence are the decider's, pinned by its own tests; here they show the record carries what it returned: Red,
    ///     write <c>fallback</c> or <c>confidence</c> from a constant, and they fail.
    /// </summary>
    [Theory]
    [InlineData("implement", "manufacture-implement", "implementer", "provider-default")]
    [InlineData("review", "manufacture-review", "reviewer", "charter-model")]
    public async Task Without_a_decision_the_turn_is_unchanged_and_the_default_is_recorded(string node, string skill, string agent, string defaultModel)
    {
        var inner = new CapturingRunner();
        var records = new InMemoryRecords();

        var result = await Runner(inner, client: null, records).RunAsync(RequestFor(RunAt(node, skill, agent)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        var sent = inner.Requests.Should().ContainSingle().Subject;
        using var payload = JsonDocument.Parse(records.Appended.Should().ContainSingle().Subject.PayloadJson);
        using (new AssertionScope())
        {
            sent.Model.Should().BeNull();
            sent.ToolFilter.Should().BeNull();
            payload.RootElement.GetProperty("model").GetString().Should().Be(defaultModel);
            payload.RootElement.GetProperty("fallback").GetString().Should().Be(NodeSelectionDecider.Off);
            payload.RootElement.GetProperty("confidence").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    /// <summary>
    ///     A redelivery of the same visit runs on what the first attempt recorded, and asks nothing. The record of an
    ///     earlier visit, at another seq, is not this visit's. Reds, each applied once: skip the read, and the decider is
    ///     called, the model is <c>strong</c>, the filter is the new one and a record is appended; match any seq instead of
    ///     <c>CurrentSeq</c>, and the earlier visit's <c>middle</c> and full list are used.
    /// </summary>
    [Fact]
    public async Task A_redelivered_dispatch_reuses_the_recorded_decision()
    {
        var inner = new CapturingRunner();
        var records = new InMemoryRecords();
        using var client = HardWithoutRoslyn();
        var run = RunAt("implement", ReviewHandoff.ImplementSkillName);
        Recorded(records, run, seq: 5, "middle", null);
        Recorded(records, run, seq: 7, "cheap", ["workspace__*", "daedalus__*"]);

        var result = await Runner(inner, client, records).RunAsync(RequestFor(run), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        var sent = inner.Requests.Should().ContainSingle().Subject;
        using (new AssertionScope())
        {
            client.Calls.Should().Be(0, "a recorded visit is never decided again");
            sent.Model.Should().Be("cheap");
            sent.ToolFilter.Should().Equal("workspace__*", "daedalus__*");
            records.Appended.Should().BeEmpty();
        }
    }

    /// <summary>
    ///     Two deliveries decided at once: this one's append loses to the unique index, so it reads the winner's record
    ///     back and runs on that, never on its own decision. Reds, each applied once: treat the violation as a failed
    ///     write, and the result is a failure; keep this attempt's decision after the violation, and the model is
    ///     <c>strong</c> and the filter the new one.
    /// </summary>
    [Fact]
    public async Task A_concurrent_duplicate_is_treated_as_recorded()
    {
        var inner = new CapturingRunner();
        var run = RunAt("implement", ReviewHandoff.ImplementSkillName);
        var winner = new InMemoryRecords();
        Recorded(winner, run, seq: 7, "cheap", ["workspace__*", "daedalus__*"]);
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(run.Id, WorkflowRunRecord.NodeSelectionKind, Arg.Any<CancellationToken>()).Returns(
            new ValueTask<IReadOnlyList<WorkflowRunRecord>>([]),
            new ValueTask<IReadOnlyList<WorkflowRunRecord>>(winner.Seeded));
        records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException(UniqueViolation(WorkflowRunRecord.NodeSelectionIndexName)));
        using var client = HardWithoutRoslyn();

        var result = await Runner(inner, client, records).RunAsync(RequestFor(run), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        var sent = inner.Requests.Should().ContainSingle().Subject;
        sent.Model.Should().Be("cheap");
        sent.ToolFilter.Should().Equal("workspace__*", "daedalus__*");
    }

    /// <summary>
    ///     From the first rejected review on, both nodes keep their full tool list; the model is still decided. Reds,
    ///     each applied once: ignore the visit counts, and the filter is narrowed; read only the review count, and the
    ///     implement row narrows; stop deciding the model after a rejection, and the model assertion fails.
    /// </summary>
    [Theory]
    [InlineData("review", "manufacture-review", "reviewer", 1, 2)]
    [InlineData("implement", "manufacture-implement", "implementer", 2, 1)]
    public async Task After_a_rejection_tools_are_not_narrowed(string node, string skill, string agent, int implementVisits, int reviewVisits)
    {
        var inner = new CapturingRunner();
        using var client = HardWithoutRoslyn();
        var visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["implement"] = implementVisits, ["review"] = reviewVisits };

        var result = await Runner(inner, client, new InMemoryRecords()).RunAsync(RequestFor(RunAt(node, skill, agent, visits)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        var sent = inner.Requests.Should().ContainSingle().Subject;
        sent.ToolFilter.Should().BeNull();
        sent.Model.Should().Be("strong");
    }

    /// <summary>
    ///     Only implement and review are decided. Retrospect, a run with no manifest, a turn that is not a workflow
    ///     turn and a role with no configuration are forwarded as they came, the same request, with nothing asked and
    ///     nothing recorded. Reds, each applied once: make every pinned node eligible, and the retrospect row is decided,
    ///     so its request is a copy, the decider is asked and a record appended; drop the manifest check, and the
    ///     no-manifest row throws; drop the role check, and the unconfigured row throws. The not-a-workflow-turn row has no mutation of its own:
    ///     the runner cannot reach the run without the <see cref="WorkflowCaller"/> check, so removing it does not compile.
    /// </summary>
    [Theory]
    [InlineData("retrospect")]
    [InlineData("no-manifest")]
    [InlineData("not-a-workflow-turn")]
    [InlineData("unconfigured-role")]
    public async Task Retrospect_and_unpinned_turns_pass_through_untouched(string shape)
    {
        var inner = new CapturingRunner();
        var records = new InMemoryRecords();
        using var client = HardWithoutRoslyn();
        var request = shape switch
        {
            "retrospect" => RequestFor(RunAt("retrospect", ReviewHandoff.RetrospectSkillName, "reviewer")),
            "no-manifest" => RequestFor(RunAt("implement", ReviewHandoff.ImplementSkillName, manifest: false)),
            "not-a-workflow-turn" => RequestFor(RunAt("implement", ReviewHandoff.ImplementSkillName)) with
            {
                Caller = new Daedalus.Agents.Scheduling.DetachedPrincipal("schedule:daedalus", ["reader"]),
            },
            "unconfigured-role" => RequestFor(RunAt("implement", ReviewHandoff.ImplementSkillName, agent: "ghost")),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "no such shape"),
        };

        var result = await Runner(inner, client, records).RunAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : "");
        using (new AssertionScope())
        {
            inner.Requests.Should().ContainSingle().Which.Should().BeSameAs(request);
            client.Calls.Should().Be(0);
            records.Appended.Should().BeEmpty();
        }
    }

    /// <summary>
    ///     The same rule as <see cref="ReviewLensRunner"/>: a decision that cannot be recorded, or a record that cannot be
    ///     read, fails the node rather than running a turn whose decision a redelivery could not reproduce. The exception
    ///     never escapes, and no turn runs. Reds, each applied once: ignore the append's failure, and the turn runs on an
    ///     unrecorded decision; filter with <c>ex is not OperationCanceledException</c> alone, and the timeout row throws;
    ///     narrow the read's catch, and the read row throws; accept any unique violation as recorded, and the other-index
    ///     row runs on the record its re-read finds; accept a payload that does not parse as no record, and the unreadable
    ///     row decides afresh and runs. The error code: return <c>AgentError.Validation</c> from the append's catch, and the append rows fail.
    /// </summary>
    [Theory]
    [InlineData("append-fails")]
    [InlineData("append-times-out")]
    [InlineData("other-unique-index")]
    [InlineData("read-fails")]
    [InlineData("record-unreadable")]
    public async Task A_record_that_cannot_be_written_fails_the_node(string failure)
    {
        var inner = new CapturingRunner();
        var run = RunAt("implement", ReviewHandoff.ImplementSkillName);
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(run.Id, WorkflowRunRecord.NodeSelectionKind, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([]));
        Exception? appendFailure = failure switch
        {
            "append-fails" => new InvalidOperationException("database is down"),
            "append-times-out" => new OperationCanceledException("command timeout"),
            "other-unique-index" => UniqueViolation("PK_WorkflowRunRecords"),
            _ => null,
        };
        if (string.Equals(failure, "other-unique-index", StringComparison.Ordinal))
        {
            // A re-read would find a decision, so only the constraint check keeps this violation from reading as "recorded".
            var other = new InMemoryRecords();
            Recorded(other, run, seq: 7, "cheap", null);
            records.ListAsync(run.Id, WorkflowRunRecord.NodeSelectionKind, Arg.Any<CancellationToken>()).Returns(
                new ValueTask<IReadOnlyList<WorkflowRunRecord>>([]),
                new ValueTask<IReadOnlyList<WorkflowRunRecord>>(other.Seeded));
        }

        if (appendFailure is not null)
        {
            records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromException(appendFailure));
        }

        if (string.Equals(failure, "read-fails", StringComparison.Ordinal))
        {
            records.ListAsync(run.Id, WorkflowRunRecord.NodeSelectionKind, Arg.Any<CancellationToken>())
                .Returns(_ => ValueTask.FromException<IReadOnlyList<WorkflowRunRecord>>(new InvalidOperationException("database is down")));
        }

        if (string.Equals(failure, "record-unreadable", StringComparison.Ordinal))
        {
            var unreadable = WorkflowRunRecord.Create(
                run.Id, 7, "implement", WorkflowRunRecord.NodeSelectionKind, "earlier", null, """{"level":"Hard"}""", Now.UtcDateTime).Value;
            records.ListAsync(run.Id, WorkflowRunRecord.NodeSelectionKind, Arg.Any<CancellationToken>())
                .Returns(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([unreadable]));
        }

        using var client = HardWithoutRoslyn();

        var act = async () => await Runner(inner, client, records).RunAsync(RequestFor(run), CancellationToken.None);

        var result = (await act.Should().NotThrowAsync()).Subject;
        result.IsFailure.Should().BeTrue("a decision that cannot be recorded or read back must not run");
        result.Error.Code.Should().Be(AgentErrorCode.StoreError);
        inner.Requests.Should().BeEmpty();
    }

    /// <summary>The caller's own cancellation still propagates. Red: catch every exception around the append.</summary>
    [Fact]
    public async Task The_callers_own_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(Guid.Empty, default, default).ReturnsForAnyArgs(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([]));
        records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>()).Returns(_ => CancelThenThrowAsync(cts));
        using var client = HardWithoutRoslyn();

        var act = async () => await Runner(new CapturingRunner(), client, records)
            .RunAsync(RequestFor(RunAt("implement", ReviewHandoff.ImplementSkillName)), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Cancels the caller's own token, then fails the append with that cancellation, as a cancelled insert does.</summary>
    private static async ValueTask CancelThenThrowAsync(CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        throw new OperationCanceledException(cts.Token);
    }

    private static DbUpdateException UniqueViolation(string constraint) =>
        new("insert failed", new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation, constraintName: constraint));
}
