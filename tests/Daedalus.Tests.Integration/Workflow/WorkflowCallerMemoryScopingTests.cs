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

    [Fact]
    public async Task A_memory_written_by_implementer_is_not_recallable_by_reviewer_in_the_same_run()
    {
        const string marker = "IMPLEMENTER-ONLY-MARKER-9c1e77";
        var scripted = new ScriptedChatClient();
        scripted.ThenToolCall("memory__remember", new { text = marker });
        scripted.ThenText("Noted.");
        scripted.ThenToolCall("memory__recall", new { query = marker });
        scripted.ThenText("Nothing relevant.");

        await using var harness = BuildHarness(scripted);
        var run = NewRun("manufacture-role-partition");
        var caller = new WorkflowCaller(run); // same run for both roles

        await harness.RunAsync(ImplementerId, "Remember something only the implementer should see.", caller);
        var reviewerTurn = await harness.RunAsync(ReviewerId, "Recall the implementer only marker.", caller);

        var recall = reviewerTurn.ToolCalls.Should().ContainSingle(tc => tc.ToolName == "memory__recall").Which;
        recall.ResultPreview.Should().NotContain(marker,
            "implementer's memory must be pinned to implementer's agent, not the shared (owner, null) partition reviewer also reads");
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
