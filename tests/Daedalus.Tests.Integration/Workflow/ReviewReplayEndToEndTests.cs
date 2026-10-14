using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos;
using Thalos.Git;
using Thalos.Testing;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.10 task B7: the <c>review-replay</c> process, through the real start endpoint, over a real
///     <see cref="LocalGitRemote"/> that holds a seeded <c>manufacture/&lt;run-id&gt;</c> branch. As in
///     <see cref="ManufactureV6EndToEndTests"/> the model is the only thing replaced: a <see cref="ScriptedChatClient"/>
///     stands in for the chat client, so each review lens reports through the real <c>daedalus__report_review_outcome</c>
///     tool and the host keeps its own runtime, workspace provider and review dispatch. The host is pinned to local mode.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ReviewReplayEndToEndTests(PostgresFixture fixture)
{
    private const string OutcomeToolName = "workflow__report_outcome";

    private const string ReviewedA = "class A\n{\n    // the reviewed branch's guard.\n    public static void Check(object? value) { }\n}\n";

    /// <summary>A path only the original run's record holds, so a turn that names it was given it by the seeding.</summary>
    private const string SeededPath = "src/Seeded/OriginalTouch.cs";

    private static readonly string[] Lenses = ["correctness", "falsifiability", "mechanism"];

    /// <summary>
    ///     A replay starts on a seeded branch, checks that branch out as its worktree, runs the three lens passes and ends
    ///     as succeeded at <c>done</c>. It never parks at the gate, opens a pull request or pushes. Red, per assertion: cut
    ///     the worktree from <c>main</c> instead of the branch, and the file assertion fails; name the replay's local branch
    ///     <c>manufacture/&lt;run&gt;</c>, and the branch assertion fails; point the review node's <c>approved</c> edge at a
    ///     gate, and the run parks and the wait for success fails; declare two lenses, and the evidence count fails; drop the
    ///     files_touched seeding in the starter, and the variable assertion and the reviewer-input assertion fail.
    ///     Guards, no isolated Red: the publisher count, the two no-branch assertions and the tip assertion hold because
    ///     no node of the graph can publish or push, and turn red only if a later change adds such a node.
    /// </summary>
    [Fact]
    public async Task A_replay_reviews_the_given_branch_and_ends()
    {
        var chat = new ScriptedChatClient();
        foreach (var lens in Lenses)
        {
            chat.ThenToolCall(DaedalusReviewTools.QualifiedReportReviewOutcomeToolName, new
            {
                lens,
                verdict = "approved",
                @checked = $$"""["src/A.cs: A.Check is reviewed ({{lens}})"]""",
            });
            chat.ThenToolCall(OutcomeToolName, new { outcome = "approved" });
            chat.ThenText("Reviewed.");
        }

        await WithHostAsync(chat, async (host, publisher) =>
        {
            var originalId = await SeedOriginalAsync(host);
            var reviewedBranch = $"manufacture/{originalId}";
            var tip = host.Remote.PushBranch(reviewedBranch, ("src/A.cs", ReviewedA));
            using var admin = host.Client("a-admin", "admin");

            var start = await admin.PostAsJsonAsync(
                "/api/workflow-runs",
                new StartWorkflowRunRequest("Review the guard.", ScratchWorkflowHost.Repository, "review-replay", reviewedBranch));
            start.StatusCode.Should().Be(HttpStatusCode.Created);
            var runId = (await start.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;

            var done = await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Succeeded, "ended at done");

            done.Process.Should().Be("review-replay");
            done.CurrentNode.Should().Be("done");
            var root = Path.Combine(host.RunsRoot, runId.ToString());
            LocalGitRemote.Git(root, "rev-parse --abbrev-ref HEAD").Should().Be($"review-replay/{runId}");
            (await File.ReadAllTextAsync(Path.Combine(root, "src", "A.cs"))).ReplaceLineEndings("\n").Should().Be(ReviewedA);
            (await host.RecordsAsync(runId, WorkflowRunRecord.ReviewEvidenceKind)).Should().HaveCount(3);
            (await host.Store.FindAsync(runId, CancellationToken.None))!.Variables.Should().ContainKey(ReviewHandoff.FilesTouchedKey);
            var reviewerInput = string.Join('\n', chat.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ChatRole.User).Select(m => m.Text));
            reviewerInput.Should().Contain(SeededPath, "the reviewer is told which files the original run touched");
            publisher.OpenCount.Should().Be(0);
            host.Remote.HasBranch($"review-replay/{runId}").Should().BeFalse("a replay pushes nothing");
            host.Remote.HasBranch($"manufacture/{runId}").Should().BeFalse("a replay has no manufacture branch of its own");
            host.Remote.HeadOf(reviewedBranch).Should().Be(tip);
        });
    }

    /// <summary>
    ///     A rejection ends the replay too: there is no implement node to send it back to, so the verdict and its findings
    ///     are the result. Red, per assertion: point <c>rejected</c> at a terminal that fails, and the run fails instead of
    ///     succeeding, so the wait fails. The evidence count of one is the lens runner stopping at the first rejection,
    ///     and the publisher count is a guard, as in the approval test.
    /// </summary>
    [Fact]
    public async Task A_rejected_replay_ends_at_the_first_rejecting_lens()
    {
        var chat = new ScriptedChatClient();
        chat.ThenToolCall(DaedalusReviewTools.QualifiedReportReviewOutcomeToolName, new
        {
            lens = "correctness",
            verdict = "rejected",
            findings = """[{"file":"src/A.cs","line":4,"scenario":"Check accepts a null argument"}]""",
        });
        chat.ThenToolCall(OutcomeToolName, new { outcome = "rejected" });
        chat.ThenText("Rejected.");

        await WithHostAsync(chat, async (host, publisher) =>
        {
            var originalId = await SeedOriginalAsync(host);
            var reviewedBranch = $"manufacture/{originalId}";
            host.Remote.PushBranch(reviewedBranch, ("src/A.cs", ReviewedA));
            using var admin = host.Client("a-admin", "admin");

            var start = await admin.PostAsJsonAsync(
                "/api/workflow-runs",
                new StartWorkflowRunRequest("Review the guard.", ScratchWorkflowHost.Repository, "review-replay", reviewedBranch));
            start.StatusCode.Should().Be(HttpStatusCode.Created);
            var runId = (await start.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;

            var done = await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Succeeded, "ended at done");

            done.CurrentNode.Should().Be("done");
            (await host.RecordsAsync(runId, WorkflowRunRecord.ReviewEvidenceKind)).Should().HaveCount(1);
            publisher.OpenCount.Should().Be(0);
        });
    }

    /// <summary>
    ///     The refusals reach the caller as 400 before a run or a worktree exists. Red, per row: drop the starter's
    ///     check for that row, and the start succeeds, so the status fails.
    /// </summary>
    [Theory]
    [InlineData("manufacture", "manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90")]
    [InlineData(null, "manufacture/0b1f6c2e-7d4a-4e53-9a1c-2f8d5e6b7a90")]
    [InlineData("review-replay", "main")]
    [InlineData("review-replay", null)]
    [InlineData("nonsense", null)]
    public async Task A_refused_process_or_base_branch_is_400_and_leaves_no_worktree(string? process, string? baseBranch)
    {
        await WithHostAsync(new ScriptedChatClient(), async (host, _) =>
        {
            using var admin = host.Client("a-admin", "admin");

            var start = await admin.PostAsJsonAsync(
                "/api/workflow-runs", new StartWorkflowRunRequest("Review the guard.", ScratchWorkflowHost.Repository, process, baseBranch));

            start.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            host.RunDirectories().Should().BeEmpty();
            await Task.CompletedTask;
        });
    }

    /// <summary>
    ///     A branch whose run does not exist is the caller's mistake: 400 naming the branch, before a worktree exists. Red:
    ///     drop the starter's run lookup, and the start goes on to the workspace and answers 500, so the status fails.
    /// </summary>
    [Fact]
    public async Task A_base_branch_of_an_unknown_run_is_400_without_a_worktree()
    {
        await WithHostAsync(new ScriptedChatClient(), async (host, _) =>
        {
            using var admin = host.Client("a-admin", "admin");

            var start = await admin.PostAsJsonAsync(
                "/api/workflow-runs",
                new StartWorkflowRunRequest(
                    "Review the guard.", ScratchWorkflowHost.Repository, "review-replay", $"manufacture/{Guid.NewGuid()}"));

            start.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await start.Content.ReadAsStringAsync()).Should().Contain("does not exist");
            host.RunDirectories().Should().BeEmpty();
            await Task.CompletedTask;
        });
    }

    /// <summary>
    ///     A run that exists but recorded no touched files cannot be replayed, and a run of another process is not a
    ///     manufacture run: both 400 before a worktree. Red, per row: drop the content check or the process check in the
    ///     starter, and the start succeeds, so the status fails.
    /// </summary>
    [Theory]
    [InlineData("manufacture", false)]
    [InlineData("review-replay", true)]
    public async Task A_run_that_cannot_be_replayed_is_400_without_a_worktree(string process, bool withFiles)
    {
        await WithHostAsync(new ScriptedChatClient(), async (host, _) =>
        {
            var originalId = await SeedOriginalAsync(host, process, withFiles);
            host.Remote.PushBranch($"manufacture/{originalId}", ("src/A.cs", ReviewedA));
            using var admin = host.Client("a-admin", "admin");

            var start = await admin.PostAsJsonAsync(
                "/api/workflow-runs",
                new StartWorkflowRunRequest(
                    "Review the guard.", ScratchWorkflowHost.Repository, "review-replay", $"manufacture/{originalId}"));

            start.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            host.RunDirectories().Should().BeEmpty();
            await Task.CompletedTask;
        });
    }

    /// <summary>
    ///     A run that exists whose branch the remote lacks fails at the workspace provider, which reports only
    ///     <c>git worktree add failed.</c>, so the endpoint answers 500: a host failure, not a 4xx. No worktree remains.
    ///     Red, per assertion: ignore the workspace's result and go on to the start, and the status is Created; report a
    ///     different failure, and the message fails.
    /// </summary>
    [Fact]
    public async Task A_base_branch_the_remote_does_not_have_fails_the_start_without_a_worktree()
    {
        await WithHostAsync(new ScriptedChatClient(), async (host, _) =>
        {
            var originalId = await SeedOriginalAsync(host);
            using var admin = host.Client("a-admin", "admin");

            var start = await admin.PostAsJsonAsync(
                "/api/workflow-runs",
                new StartWorkflowRunRequest(
                    "Review the guard.", ScratchWorkflowHost.Repository, "review-replay", $"manufacture/{originalId}"));

            start.IsSuccessStatusCode.Should().BeFalse("the remote has no such branch");
            (await start.Content.ReadAsStringAsync()).Should().Contain("could not prepare the run's workspace");
            host.RunDirectories().Should().BeEmpty();
            await Task.CompletedTask;
        });
    }

    /// <summary>
    ///     Records a finished run of <paramref name="process"/> that holds <see cref="SeededPath"/> as its
    ///     <c>files_touched</c>, the value the original manufacture run's review was given. It starts at the gate so no
    ///     turn is dispatched for it.
    /// </summary>
    private static async Task<Guid> SeedOriginalAsync(ScratchWorkflowHost host, string process = "manufacture", bool withFiles = true)
    {
        var id = Guid.NewGuid();
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (withFiles)
        {
            variables[ReviewHandoff.FilesTouchedKey] = new[] { SeededPath };
        }

        var started = await host.Store.StartAsync(
            new WorkflowStartRequest
            {
                Process = process,
                Version = 1,
                CorrelationKey = $"seed:{id:N}",
                StartNode = "gate",
                InitialVariables = variables,
                StartedBy = new RunPrincipal("seed", []),
                RunId = id,
            },
            CancellationToken.None);
        started.IsSuccess.Should().BeTrue("the original run must be recorded for the replay to find");
        return id;
    }

    private async Task WithHostAsync(ScriptedChatClient chat, Func<ScratchWorkflowHost, FakePullRequestPublisher, Task> body)
    {
        var publisher = new FakePullRequestPublisher();
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            runtime: null,
            settings: new Dictionary<string, string?>(StringComparer.Ordinal) { ["Thalos:Workflow:Sandbox:Enabled"] = "false" },
            configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IChatClientProvider>(new ScriptedChatClientProvider(chat)));

                services.RemoveAll<WorkflowOutboxDispatchOptions>();
                services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });

                services.RemoveAll<IPullRequestPublisher>();
                services.RemoveAll<IOpenPullRequestLookup>();
                services.AddSingleton<IPullRequestPublisher>(publisher);
                services.AddSingleton<IOpenPullRequestLookup>(publisher);
            });

        await body(host, publisher);
    }

    private sealed class ScriptedChatClientProvider(IChatClient client) : IChatClientProvider
    {
        public string Name => "scripted";

        public string DefaultModel => "scripted-model";

        public IChatClient CreateChatClient(AgentDefinition agent) => client;
    }
}
