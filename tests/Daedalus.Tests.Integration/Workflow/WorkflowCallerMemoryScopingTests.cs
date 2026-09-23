using Daedalus.Agents.Workflow;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Thalos.Memory;
using Thalos.Testing;
using Thalos.Workflow;
using ZeroAlloc.Authorization;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Task B3: proves <see cref="WorkflowCaller"/>'s <c>IMemoryOwner</c> implementation actually produces the
///     per-role memory partition it exists for — not against a mock of Thalos's memory scoping, but against a
///     real <see cref="ISubagentRunner"/>, a real in-memory <c>IMemoryService</c>, and a <see cref="ScriptedChatClient"/>
///     standing in for the model, so a memory is written and read back through the exact <c>memory__remember</c>/
///     <c>memory__recall</c> tool path and the auto-recall context provider a live run would use. No database is
///     involved — <c>UseMemory()</c>'s default store and session store are both in-process — so this class carries
///     no <c>[Collection(DatabaseCollection.Name)]</c> and does not need Docker.
/// </summary>
/// <remarks>
///     A <see cref="HashedBagOfWordsEmbeddingGenerator"/> is registered so recall hits the
///     <see cref="MemoryRecallTier.Semantic"/> path instead of degrading to <see cref="MemoryRecallTier.Recency"/>.
///     This matters for assertions, not just realism: the tool-call pipeline trims <c>ToolCallSummary.ResultPreview</c>
///     to 200 characters (<c>Thalos.Tools.AuthorizingAIFunction.PreviewLength</c>), and the Recency tier's own
///     "index was unavailable" note alone is close to 200 characters — long enough that a recalled memory's text
///     is silently cut off before an assertion ever sees it, which would make a scoping bug and a truncated
///     preview look identical. Remembered text and the recall query below use the same marker token so their
///     bag-of-words cosine similarity is 1.0, comfortably clearing <c>RecallOptions.MinScore</c>'s 0.6 default.
/// </remarks>
public sealed class WorkflowCallerMemoryScopingTests
{
    private static readonly AgentId ImplementerId = new(Guid.NewGuid());
    private static readonly AgentId ReviewerId = new(Guid.NewGuid());

    [Fact]
    public async Task A_memory_written_by_reviewer_in_one_run_is_recallable_by_reviewer_in_the_next_run()
    {
        const string marker = "REVIEWER-CARRYOVER-MARKER-7f3a21";
        var scripted = new ScriptedChatClient();
        scripted.ThenToolCall("memory__remember", new { text = marker });
        scripted.ThenText("Noted.");
        scripted.ThenToolCall("memory__recall", new { query = marker });
        scripted.ThenText("Recalled.");

        await using var harness = BuildHarness(scripted);
        var run1 = NewRun("manufacture-recall-carryover");
        var run2 = NewRun("manufacture-recall-carryover"); // same process, a later run

        await harness.RunAsync(ReviewerId, "Remember something for later runs.", new WorkflowCaller(run1));
        var second = await harness.RunAsync(ReviewerId, "Recall the carryover marker.", new WorkflowCaller(run2));

        var recall = second.ToolCalls.Should().ContainSingle(tc => tc.ToolName == "memory__recall").Which;
        recall.ResultPreview.Should().Contain(marker,
            "reviewer's memory owner is the process, not the run, so run2 must read back what run1 wrote");
    }

    /// <summary>
    ///     Design section 10's second row, both halves. The negative half alone is the vacuous shape section 10
    ///     itself flags — "a scoping test over an empty store passes whatever the filter says" — so the
    ///     implementer recalls its own marker first. That turn is what proves the write landed in a partition
    ///     something can read; without it, a scripted run that wrote nothing at all satisfies the reviewer's
    ///     assertion perfectly. Found exactly that way: replacing the implementer's <c>memory__remember</c> step
    ///     with a plain text response left this test green.
    /// </summary>
    [Fact]
    public async Task A_memory_written_by_implementer_is_recallable_by_implementer_and_not_by_reviewer_in_the_same_run()
    {
        const string marker = "IMPLEMENTER-ONLY-MARKER-9c1e77";
        var scripted = new ScriptedChatClient();
        scripted.ThenToolCall("memory__remember", new { text = marker });
        scripted.ThenText("Noted.");
        scripted.ThenToolCall("memory__recall", new { query = marker });
        scripted.ThenText("Mine.");
        scripted.ThenToolCall("memory__recall", new { query = marker });
        scripted.ThenText("Nothing relevant.");

        await using var harness = BuildHarness(scripted);
        var run = NewRun("manufacture-role-partition");
        var caller = new WorkflowCaller(run); // same run for both roles

        await harness.RunAsync(ImplementerId, "Remember something only the implementer should see.", caller);
        var implementerRecall = await harness.RunAsync(ImplementerId, "Recall your own marker.", caller);
        var reviewerTurn = await harness.RunAsync(ReviewerId, "Recall the implementer only marker.", caller);

        // The seeding assertion. Falsifiable in the direction that matters: replacing the remember step with a
        // plain text response turns this red, which is what the negative assertion below cannot do.
        implementerRecall.ToolCalls.Should().ContainSingle(tc => tc.ToolName == "memory__recall")
            .Which.ResultPreview.Should().Contain(marker,
                "the implementer must read back what it wrote, or the isolation below is asserted over an empty store");

        var recall = reviewerTurn.ToolCalls.Should().ContainSingle(tc => tc.ToolName == "memory__recall").Which;
        recall.ResultPreview.Should().NotContain(marker,
            "implementer's memory must be pinned to implementer's agent, not the shared (owner, null) partition reviewer also reads");
    }

    /// <summary>
    ///     The "own plus shared" half of design section 10's second row, which nothing covered. A filter that
    ///     returned <em>nothing at all</em> to the reviewer satisfies "never the implementer's" perfectly and is
    ///     useless, so the two partitions the reviewer is supposed to read are asserted present in the same
    ///     seeded store the implementer's row is asserted absent from.
    /// </summary>
    /// <remarks>
    ///     The three rows are seeded through <see cref="IMemoryService"/> directly rather than through three
    ///     scripted turns, because a workflow turn cannot write the owner-wide partition at all:
    ///     <see cref="WorkflowCaller.PinMemoriesToAgent"/> is <see langword="true"/>, which is design decision
    ///     D13 and the thing this test must not quietly undo in order to set itself up. Each marker is recalled
    ///     by its own query so every lookup is an exact text match and clears <c>RecallOptions.MinScore</c>,
    ///     rather than one query that has to rank three different texts at once.
    /// </remarks>
    [Fact]
    public async Task A_reviewer_recalls_its_own_and_the_owner_wide_partition_but_never_the_implementers()
    {
        const string reviewerMarker = "REVIEWER-OWN-MARKER-4d9b02";
        const string sharedMarker = "OWNER-WIDE-MARKER-1a7c5e";
        const string implementerMarker = "IMPLEMENTER-PINNED-MARKER-6e2f84";

        var scripted = new ScriptedChatClient();
        foreach (var marker in new[] { reviewerMarker, sharedMarker, implementerMarker })
        {
            scripted.ThenToolCall("memory__recall", new { query = marker });
            scripted.ThenText("Looked.");
        }

        await using var harness = BuildHarness(scripted);
        var run = NewRun("manufacture-own-plus-shared");
        var caller = new WorkflowCaller(run);

        await harness.SeedAsync(caller.MemoryOwnerId, ReviewerId, reviewerMarker);
        await harness.SeedAsync(caller.MemoryOwnerId, agentId: null, sharedMarker);
        await harness.SeedAsync(caller.MemoryOwnerId, ImplementerId, implementerMarker);

        var own = await harness.RunAsync(ReviewerId, "Recall your own note.", caller);
        var shared = await harness.RunAsync(ReviewerId, "Recall the owner-wide note.", caller);
        var foreign = await harness.RunAsync(ReviewerId, "Recall the implementer's note.", caller);

        // Falsifiable per partition. Seeding the reviewer's row under the implementer's agent instead turns the
        // first red; seeding the owner-wide row under an agent pin turns the second red; seeding the
        // implementer's row with no agent pin at all - which is the shape WorkflowCaller would produce if it
        // stopped pinning - turns the third red.
        own.ToolCalls.Should().ContainSingle(tc => tc.ToolName == "memory__recall")
            .Which.ResultPreview.Should().Contain(reviewerMarker, "a role must read what it pinned to itself");
        shared.ToolCalls.Should().ContainSingle(tc => tc.ToolName == "memory__recall")
            .Which.ResultPreview.Should().Contain(sharedMarker, "and the owner-wide partition every role of this owner reads");
        foreign.ToolCalls.Should().ContainSingle(tc => tc.ToolName == "memory__recall")
            .Which.ResultPreview.Should().NotContain(implementerMarker, "but never a row pinned to the other role");
    }

    [Fact]
    public async Task Auto_recall_reads_the_same_owner_as_explicit_recall()
    {
        const string marker = "AUTO-RECALL-PARITY-MARKER-2b6d44";
        var scripted = new ScriptedChatClient();
        scripted.ThenToolCall("memory__remember", new { text = marker });
        scripted.ThenText("Noted.");
        scripted.ThenText("Ok."); // run2's only response: no tool call, so any recalled text came from auto-recall alone

        await using var harness = BuildHarness(scripted);
        var run1 = NewRun("manufacture-autorecall-parity");
        var run2 = NewRun("manufacture-autorecall-parity");

        await harness.RunAsync(ReviewerId, "Remember something for the agenda.", new WorkflowCaller(run1));
        await harness.RunAsync(ReviewerId, "What is on the agenda?", new WorkflowCaller(run2));

        // The last request is run2's only model call. Its Options.Instructions come solely from
        // MemoryContextProvider (auto-recall) — the script never gives the model a chance to call
        // memory__recall in this turn — so the marker's presence here proves auto-recall resolved the
        // same owner an explicit memory__recall call would (Part A's own fix-round bug: the two read
        // paths disagreeing because only one of them was updated to honour IMemoryOwner). Auto-recall's
        // query is the user text above, which shares no tokens with the marker, so this exercises the
        // Recency-tier fallback rather than semantic ranking — irrelevant here since ChatOptions.Instructions
        // carries the full MemoryRecallBlock.Render() output, never trimmed the way a tool result is.
        var lastRequest = scripted.Requests[^1];
        lastRequest.Options?.Instructions.Should().Contain(marker);
    }

    private static WorkflowRun NewRun(string process) => new()
    {
        Id = Guid.NewGuid(),
        Process = process,
        ProcessVersion = 1,
        CurrentNode = "start",
        CurrentSeq = 0,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
    };

    /// <summary>
    ///     A real Thalos agent runtime (session store, memory service, agent factory — all wired by
    ///     <c>AddThalos</c> exactly as production does) with only the chat client swapped for
    ///     <paramref name="scripted"/>, matching <c>ScheduledRunFlowTests.BuildExecutor</c>'s pattern.
    /// </summary>
    private static Harness BuildHarness(ScriptedChatClient scripted)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashedBagOfWordsEmbeddingGenerator());
        services.AddThalos(thalos =>
        {
            thalos.UseChatClientProvider(new ScriptedChatClientProvider(scripted));
            thalos.UseInMemorySessionStore();
            thalos.UseMemory();
            thalos.AddAgent(new AgentDefinition { Id = ImplementerId, Name = "implementer", Instructions = "You implement." });
            thalos.AddAgent(new AgentDefinition { Id = ReviewerId, Name = "reviewer", Instructions = "You review." });
        });

        var provider = services.BuildServiceProvider();
        return new Harness(provider);
    }

    private sealed class ScriptedChatClientProvider(IChatClient client) : IChatClientProvider
    {
        public string Name => "scripted";

        public string DefaultModel => "scripted-model";

        public IChatClient CreateChatClient(AgentDefinition agent) => client;
    }

    /// <summary>One DI container shared across a test's calls, so its in-memory <c>IMemoryStore</c> instance persists between them — the same fiction "the next run" needs.</summary>
    private sealed class Harness(ServiceProvider provider) : IAsyncDisposable
    {
        /// <summary>
        ///     Writes one memory straight into the store, in the partition named by <paramref name="agentId"/>
        ///     — <see langword="null"/> for the owner-wide one. Needed because a workflow turn cannot produce an
        ///     owner-wide write at all: <c>WorkflowCaller.PinMemoriesToAgent</c> is <see langword="true"/>, so
        ///     <c>MemoryTools.RememberAsync</c> pins every memory such a turn writes to its own agent.
        /// </summary>
        public async Task SeedAsync(string ownerId, AgentId? agentId, string text)
        {
            var memory = provider.GetRequiredService<IMemoryService>();
            var result = await memory.RememberAsync(
                new RememberRequest { OwnerId = ownerId, AgentId = agentId, Text = text }, CancellationToken.None);
            result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : null);
        }

        public async Task<AgentTurnResult> RunAsync(AgentId agentId, string task, ISecurityContext caller)
        {
            var runner = provider.GetRequiredService<ISubagentRunner>();
            var result = await runner.RunAsync(new SubagentRunRequest { AgentId = agentId, Task = task, Caller = caller });
            result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : null);
            return result.Value;
        }

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }
}
