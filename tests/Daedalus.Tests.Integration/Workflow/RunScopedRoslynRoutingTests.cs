using System.Security.Claims;
using System.Text.Json;
using Daedalus.Agents;
using Daedalus.Agents.Security;
using Daedalus.Agents.Workflow;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Testing;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Task B9's security questions about where a <c>roslyn__*</c> call lands, answered against real
///     <c>RoslynCodeLens.Mcp</c> servers started through <c>dnx</c>: one host server over a host solution, and one server
///     per run over that run's own worktree. The <c>roslyn</c> entry is the shipped <c>.mcp.json</c>'s, <c>runScoped</c>
///     block and all; only the host server's solution is swapped for a small one, so the test does not load
///     <c>Daedalus.sln</c>. Each call goes through a real Thalos runtime turn, so the routing key is the turn's caller,
///     exactly as in production: <see cref="WorkflowNodeDispatcherFactory.CreateCallerResolver"/> for a run, and
///     <see cref="ClaimsSecurityContext"/> for a chat turn.
/// </summary>
/// <remarks>
///     <para>
///     <c>list_solutions</c> answers with the path of the solution its server loaded, so its answer names the server
///     that served the call. The run's worktree lies under <c>runs/&lt;run-id&gt;</c>, so a path naming a run's id is
///     that run's server; a path naming <c>HostApp.sln</c> is the host server.
///     </para>
///     <para>
///     Routing is by source, never by tool, so what holds for <c>list_solutions</c> holds for <c>apply_code_action</c>:
///     both are tools of the one <c>RunScopedMcpToolSource</c>.
///     </para>
///     <para>
///     Needs the .NET 10 SDK's <c>dnx</c> on the path and the <c>RoslynCodeLens.Mcp</c> package from NuGet, as the
///     shipped <c>.mcp.json</c> does. A missing tool fails the test when the servers do not come up; nothing skips it.
///     </para>
/// </remarks>
public sealed class RunScopedRoslynRoutingTests : IAsyncLifetime
{
    private static readonly AgentId ArchitectId = new(Guid.NewGuid());

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(5);

    private readonly LocalGitRemote _remote = LocalGitRemote.Create(TinySolution("Tiny.sln"));

    private readonly string _hostDirectory = Directory.CreateTempSubdirectory("daedalus-roslyn-host-").FullName;

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("daedalus-roslyn-runs-").FullName;

    private readonly ScriptedChatClient _chat = new();

    private readonly Guid _runA = Guid.NewGuid();

    private readonly Guid _runB = Guid.NewGuid();

    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        foreach (var (path, content) in TinySolution("HostApp.sln"))
        {
            var full = Path.Combine(_hostDirectory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
        }

        var shipped = McpConfigFile.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, ".mcp.json")))["roslyn"];
        shipped.RunScoped.Should().NotBeNull("the shipped roslyn entry must be run-scoped");
        var roslyn = new McpServerDefinition
        {
            Type = shipped.Type,
            Command = shipped.Command,
            Args = ["RoslynCodeLens.Mcp", "--yes", "--", Path.Combine(_hostDirectory, "HostApp.sln")],
            Env = shipped.Env,
            Timeout = shipped.Timeout,
            ShutdownTimeout = shipped.ShutdownTimeout,
            RunScoped = shipped.RunScoped,
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowConfig());
        services.AddThalos(thalos =>
        {
            thalos.UseChatClientProvider(new ScriptedChatClientProvider(_chat));
            thalos.UseInMemorySessionStore();
            thalos.UseGitWorktreeWorkspaces(o => o.DataRoot = _dataRoot);
            thalos.AddMcpServer("roslyn", roslyn);
            thalos.AddAgent(new AgentDefinition { Id = ArchitectId, Name = "architect", Instructions = "You inspect code.", Tools = ["roslyn__*"] });
        });
        _services = services.BuildServiceProvider();

        var workspaces = _services.GetRequiredService<IRunWorkspaceProvider>();
        var readiness = _services.GetRequiredService<IRunToolServerReadiness>();
        foreach (var runId in new[] { _runA, _runB })
        {
            var created = await workspaces.CreateAsync(
                new RunWorkspaceRequest(runId, "sandbox", _remote.Url, "main", $"manufacture/{runId}", "Tiny.sln"), CancellationToken.None);
            created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.Message : null);
        }

        foreach (var runId in new[] { _runA, _runB })
        {
            var ready = await readiness.WaitAllReadyAsync(runId, ReadyTimeout, CancellationToken.None);
            ready.IsSuccess.Should().BeTrue(ready.IsFailure ? ready.Error.Message : null);
        }
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        _remote.Dispose();
        LocalGitRemote.DeleteReadOnly(_dataRoot);
        LocalGitRemote.DeleteReadOnly(_hostDirectory);
    }

    /// <summary>
    ///     Security question 1: a run's <c>roslyn__*</c> call reaches its own run's server, never the host solution and
    ///     never another run's server. Red for the first assertion: drop the <c>runScoped</c> block from the shipped
    ///     <c>.mcp.json</c>; the host server then answers. Red for all three: build <see cref="WorkflowCaller"/>'s
    ///     <c>thalos.run_id</c> claim from run B's id.
    /// </summary>
    [Fact]
    public async Task A_runs_roslyn_call_is_served_by_its_own_runs_server_only()
    {
        var caller = WorkflowNodeDispatcherFactory.CreateCallerResolver(_services)(Run(_runA));

        var answer = await ListSolutionsAsync(caller);

        answer.Should().Contain(_runA.ToString(), "run A's server loaded run A's worktree");
        answer.Should().NotContain(_runB.ToString(), "run B's server must never serve run A");
        answer.Should().NotContain("HostApp.sln", "the host server must never serve a run");
    }

    /// <summary>
    ///     Security question 2: a developer's chat turn, even one whose token forges run A's <c>thalos.run_id</c>, is
    ///     served by the host server and never reaches a run's server, so its <c>roslyn__apply_*</c> cannot change a
    ///     run's worktree. Red: remove the <c>thalos.*</c> strip from <see cref="ClaimsSecurityContext"/>; the forged
    ///     claim then routes the call to run A's server.
    /// </summary>
    [Fact]
    public async Task A_developer_chat_turn_carrying_a_forged_run_claim_is_served_by_the_host_server()
    {
        var chat = new ClaimsSecurityContext(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "u-chat"),
                new Claim("roles", "developer"),
                new Claim("roles", "admin"),
                new Claim(RunWorkspaceClaims.RunId, _runA.ToString()),
            ],
            authenticationType: "Bearer")));

        var answer = await ListSolutionsAsync(chat);

        answer.Should().Contain("HostApp.sln");
        answer.Should().NotContain(_runA.ToString());
    }

    /// <summary>Runs one turn whose model calls <c>roslyn__list_solutions</c>, and returns what the tool answered.</summary>
    private async Task<string> ListSolutionsAsync(ISecurityContext caller)
    {
        _chat.ThenToolCall("roslyn__list_solutions", new { });
        _chat.ThenText("Listed.");
        var result = await _services.GetRequiredService<ISubagentRunner>().RunAsync(
            new SubagentRunRequest { AgentId = ArchitectId, Task = "List the loaded solutions.", Caller = caller }, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : null);

        var toolResult = _chat.Requests[^1].Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Should().ContainSingle("the model's one tool call must have been answered").Subject;
        return JsonSerializer.Serialize(toolResult.Result);
    }

    private static WorkflowRun Run(Guid id) => new()
    {
        Id = id,
        Process = "manufacture",
        ProcessVersion = 1,
        CurrentNode = "implement",
        CurrentSeq = 1,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
    };

    /// <summary>A one-project solution with no package references, which RoslynCodeLens loads in seconds.</summary>
    private static (string Path, string Content)[] TinySolution(string solutionName) =>
    [
        (solutionName, """

            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib\Lib.csproj", "{6E2C5B1A-4E0B-4C57-9C3E-2F1D6A8B7C11}"
            EndProject
            Global
            EndGlobal
            """),
        ("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
        ("Lib/Class1.cs", "namespace Lib; public class Class1 { }"),
    ];

    private sealed class ScriptedChatClientProvider(IChatClient client) : IChatClientProvider
    {
        public string Name => "scripted";

        public string DefaultModel => "scripted-model";

        public IChatClient CreateChatClient(AgentDefinition agent) => client;
    }
}
