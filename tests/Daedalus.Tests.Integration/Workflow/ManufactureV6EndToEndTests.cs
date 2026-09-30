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
///     Task B16: process v6 from a work intent to one pushed branch and one pull request, through the real start and
///     resume endpoints. Unlike <see cref="ManufactureSeamEndToEndTests"/>, the model is the only thing replaced: the
///     host keeps its own <c>ThalosAgentRuntime</c> and a <see cref="ScriptedChatClient"/> stands in for the chat
///     client, so implement's writes go through the real tool catalog, the <c>workspace-write</c> authorizer, the
///     run's write grant, the extension allow-list and the write audit, and each review lens reports through the real
///     <c>daedalus__report_review_outcome</c> tool. The pull-request host is <see cref="FakePullRequestPublisher"/>;
///     the push goes to a real <see cref="LocalGitRemote"/>.
/// </summary>
/// <remarks>
///     <para>
///     The script is one queue for the whole run, consumed in dispatch order: implement, three review lens passes,
///     retrospect. Every turn ends with a text reply, because the model is asked again after each tool result.
///     </para>
///     <para>
///     Red-making changes, each verified and reverted (see the task B16 report): adding <c>.csproj</c> to the write
///     grant's extensions writes <c>Sandbox.csproj</c>; removing the write grant leaves <c>src/A.cs</c> unedited;
///     resuming without applying the standing instructions makes the code commit the newest; unregistering the
///     <c>open-pull-request</c> action fails the start; skipping the open-pull-request lookup on a redelivery opens a
///     second pull request; a resume endpoint that records a constant principal, or the caller without its roles,
///     names the wrong approver; dropping implement's summary leaves the body's placeholder in its place; a code commit
///     that does not exclude AGENT.md leaves AGENT.md in the older commit; and an action that stores the URL under
///     another key leaves no <c>pr_url</c>.
///     </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ManufactureV6EndToEndTests(PostgresFixture fixture)
{
    /// <summary>The engine's outcome tool, the name every task node's turn is offered.</summary>
    private const string OutcomeToolName = "workflow__report_outcome";

    private const string Summary = "Tightened the guard in A: a null argument is refused.";

    private const string LearnedLine = "Integration tests need Docker running.";

    private const string PinnedStandingInstructions = "Run dotnet test.";

    private const string EditedA = "class A\n{\n    // guard: refuse a null argument before it reaches the body.\n    public static void Check(object? value) => System.ArgumentNullException.ThrowIfNull(value);\n}\n";

    private const string ExecCsproj = "<Project><Target Name=\"X\" BeforeTargets=\"Build\"><Exec Command=\"echo pwned\" /></Target></Project>";

    private static readonly string[] Lenses = ["correctness", "falsifiability", "mechanism"];

    private static readonly string[] FilesTouched = ["src/A.cs"];

    private static readonly string[] Learnings = [LearnedLine];

    [Fact]
    public async Task A_v6_run_goes_from_intent_to_one_pushed_branch_and_one_PR_with_AGENT_md_committed_separately()
    {
        await WithV6HostAsync(async host =>
        {
            var start = await host.AdminClient.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest("Tighten a guard.", "sandbox"));
            start.StatusCode.Should().Be(HttpStatusCode.Created);
            var runId = (await start.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;

            await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");
            (await File.ReadAllTextAsync(Path.Combine(host.DataRoot, "runs", runId.ToString(), "src", "A.cs"))).Should().Contain("guard", "implement really edited the worktree");
            File.Exists(Path.Combine(host.DataRoot, "runs", runId.ToString(), "Sandbox.csproj")).Should().BeFalse("the .csproj write was refused by the extension allow-list");
            host.ToolResults("workspace__write_file").Should().ContainSingle(r => r.Contains("extension '.csproj'"));

            // Both calls passed the grant, so both are audited; the allow-list refused the first inside the tool.
            (await host.Records(runId, WorkflowRunRecord.WorkspaceWriteKind)).Select(r => r.Node).Should().Equal("implement", "implement");
            (await host.Records(runId, WorkflowRunRecord.ReviewEvidenceKind)).Should().HaveCount(3);

            var resume = await host.AdminClient.PostAsJsonAsync($"/api/workflow-runs/{runId}/resume", new ResumeWorkflowRunRequest("human_approval", null, ApplyStandingInstructions: true));
            resume.StatusCode.Should().Be(HttpStatusCode.NoContent);

            var done = await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Succeeded, "published");
            done.Variables.Should().ContainKey("pr_url").WhoseValue.Should().Be(FakePullRequestPublisher.Url);
            host.Publisher.OpenCount.Should().Be(1);

            var commits = host.Remote.Log($"manufacture/{runId}", count: 2);
            commits[0].Files.Should().Equal("AGENT.md");
            host.Remote.Show($"manufacture/{runId}", "AGENT.md").Should().EndWith(LearnedLine);
            commits[1].Files.Should().Contain("src/A.cs").And.NotContain("AGENT.md");
            host.Publisher.LastBody.Should().Contain("Approved at the gate by a-admin", "the resuming principal, recorded by B1 step 2a, names the approver")
                .And.Contain(runId.ToString()).And.Contain("## Agent-written summary")
                .And.Contain(Summary, "the body quotes implement's own summary, not the placeholder for a missing one");
            done.LastResume!.By!.Id.Should().Be("a-admin");

            // The ledger carry from B1: the approver is the real caller principal, roles and all, not only its id.
            done.LastResume.By.Roles.Should().Equal(["admin"], "the resume records the caller's own roles");
        });
    }

    /// <summary>
    ///     The model's script, in dispatch order. Implement tries a <c>.csproj</c> first, which the grant's extension
    ///     allow-list refuses (ruling R29), then edits <c>src/A.cs</c>. Each review lens reports its evidence and then
    ///     the same verdict. Retrospect proposes the pinned text plus the implementer's learning.
    /// </summary>
    private static ScriptedChatClient Script()
    {
        var chat = new ScriptedChatClient();

        chat.ThenToolCall("workspace__write_file", new { path = "Sandbox.csproj", content = ExecCsproj });
        chat.ThenToolCall("workspace__write_file", new { path = "src/A.cs", content = EditedA });
        chat.ThenToolCall(OutcomeToolName, new
        {
            outcome = "changed",
            variables = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ReviewHandoff.SummaryKey] = Summary,
                [ReviewHandoff.FilesTouchedKey] = FilesTouched,
                [ReviewHandoff.LearningsKey] = Learnings,
            },
        });
        chat.ThenText("Implemented.");

        foreach (var lens in Lenses)
        {
            chat.ThenToolCall(DaedalusReviewTools.QualifiedReportReviewOutcomeToolName, new
            {
                lens,
                verdict = "approved",
                @checked = $$"""["src/A.cs: A.Check refuses a null argument ({{lens}})"]""",
            });
            chat.ThenToolCall(OutcomeToolName, new { outcome = "approved" });
            chat.ThenText("Reviewed.");
        }

        chat.ThenToolCall(OutcomeToolName, new
        {
            outcome = ReviewHandoff.RetrospectProposedOutcome,
            variables = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ReviewHandoff.ProposedStandingInstructionsKey] = PinnedStandingInstructions + "\n" + LearnedLine,
            },
        });
        chat.ThenText("Proposed.");

        return chat;
    }

    /// <summary>
    ///     Boots a <see cref="ScratchWorkflowHost"/> with its own runtime, the scripted model, a fast outbox poll, and one
    ///     <see cref="FakePullRequestPublisher"/> as both pull-request interfaces (ruling R28a). The default seed gives
    ///     <c>main</c> <c>AGENT.md</c> and <c>src/A.cs</c>. The write grant is the shipped one in
    ///     <c>src/Daedalus.Api/appsettings.json</c>, implement over <c>.cs</c> and <c>.md</c>: the test sets none of its
    ///     own, because configuration arrays merge by index, so a test entry could only add to that grant, never narrow it.
    /// </summary>
    private async Task WithV6HostAsync(Func<V6Host, Task> body)
    {
        var chat = Script();
        var publisher = new FakePullRequestPublisher();
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            runtime: null,
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

        using var admin = host.Client("a-admin", "admin");
        await body(new V6Host(host, admin, publisher, chat));
    }

    /// <summary>What the test body reaches: the booted host, the admin caller that starts and resumes, and the fakes.</summary>
    private sealed class V6Host(ScratchWorkflowHost host, HttpClient adminClient, FakePullRequestPublisher publisher, ScriptedChatClient chat)
    {
        public HttpClient AdminClient => adminClient;

        public FakePullRequestPublisher Publisher => publisher;

        public LocalGitRemote Remote => host.Remote;

        public string DataRoot => host.DataRoot;

        public Task<WorkflowRun> WaitForAsync(Guid runId, Func<WorkflowRun, bool> until, string what) =>
            host.WaitForAsync(runId, until, what);

        public Task<IReadOnlyList<WorkflowRunRecord>> Records(Guid runId, string kind) => host.RecordsAsync(runId, kind);

        /// <summary>
        ///     The result text of every call the model made to <paramref name="toolName"/>, in call order, as the model
        ///     was sent it: each call's <see cref="FunctionResultContent"/> in a later request, matched by call id.
        /// </summary>
        public IReadOnlyList<string> ToolResults(string toolName)
        {
            var messages = chat.Requests.SelectMany(r => r.Messages).SelectMany(m => m.Contents).ToList();
            var callIds = messages.OfType<FunctionCallContent>()
                .Where(c => string.Equals(c.Name, toolName, StringComparison.Ordinal))
                .Select(c => c.CallId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var results = new List<string>(callIds.Count);
            foreach (var id in callIds)
            {
                var result = messages.OfType<FunctionResultContent>().FirstOrDefault(r => string.Equals(r.CallId, id, StringComparison.Ordinal));
                result.Should().NotBeNull($"the model's '{toolName}' call '{id}' must have been answered before the model was asked again");
                results.Add(result!.Result?.ToString() ?? "");
            }

            return [.. results];
        }
    }

    private sealed class ScriptedChatClientProvider(IChatClient client) : IChatClientProvider
    {
        public string Name => "scripted";

        public string DefaultModel => "scripted-model";

        public IChatClient CreateChatClient(AgentDefinition agent) => client;
    }
}
