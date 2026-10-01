using System.Text.Json;
using Daedalus.Agents.Workflow;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Task B9, ruling R29, from the shipped configuration end to end: a real, workflow-enabled Api host booted from the
///     shipped <c>appsettings.json</c> with no override of <c>WriteGrants</c>; a run started through the registered
///     <see cref="IManufactureRunStarter"/> by a developer; the caller from the dispatcher's own resolver; and the host's
///     own <see cref="WorkspaceTools"/>, over the registered <see cref="RunWorkspaceToolOptions"/>. MSBuild evaluates
///     <c>Directory.Build.props</c> when the run's Roslyn server loads or rebuilds the worktree, so a run that could write
///     it could run code on the host.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WorkspaceExtensionBoundaryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private ScratchWorkflowHost _host = null!;

    public async Task InitializeAsync() => _host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>
    ///     Red for the refusal and the file: add <c>".props"</c> to the shipped <c>AllowedExtensions</c> in
    ///     <c>src/Daedalus.Api/appsettings.json</c>. Red for the last line: pass an empty set, or one built from something
    ///     other than <c>WriteGrants</c>, as the ceiling in <c>UseRunWorkspaceTools</c>.
    /// </summary>
    [Fact]
    public async Task Implement_of_a_developer_started_run_cannot_write_Directory_Build_props()
    {
        var run = await StartedRunAsync(_host, new RunPrincipal("u-dev", ["developer"]));
        var caller = WorkflowNodeDispatcherFactory.CreateCallerResolver(_host.Factory.Services)(run);
        var tools = ActivatorUtilities.CreateInstance<WorkspaceTools>(_host.Factory.Services);
        var root = Path.Combine(_host.RunsRoot, run.Id.ToString());
        Directory.Exists(root).Should().BeTrue("the run's worktree is where the write would land");

        (await Authorize(_host, caller, "workspace__write_file")).Allowed.Should().BeTrue("the grant holds; the extension is what refuses");
        (await tools.WriteFile(caller, "Directory.Build.props", "<Project><Target Name=\"X\" BeforeTargets=\"Build\"><Exec Command=\"calc\" /></Target></Project>"))
            .Should().Contain("extension '.props'");
        File.Exists(Path.Combine(root, "Directory.Build.props")).Should().BeFalse();
        (await tools.WriteFile(caller, "src/A.cs", "class A { }")).Should().StartWith("wrote", "the same caller may still write code");
    }

    /// <summary>A run started through the registered starter, positioned at <c>implement</c>, its first node.</summary>
    private static async Task<WorkflowRun> StartedRunAsync(ScratchWorkflowHost host, RunPrincipal startedBy)
    {
        var started = await host.Factory.Services.GetRequiredService<IManufactureRunStarter>().StartAsync(
            new ManufactureStartRequest("Tighten a guard.", ScratchWorkflowHost.Repository, startedBy), CancellationToken.None);
        started.IsSuccess.Should().BeTrue(started.IsFailure ? started.Error : null);
        var run = await host.Store.FindAsync(started.Value, CancellationToken.None);
        return run! with { CurrentNode = "implement" };
    }

    private static async Task<ToolAuthorizationDecision> Authorize(ScratchWorkflowHost host, ISecurityContext caller, string tool) =>
        await host.Factory.Services.GetRequiredService<IToolAuthorizer>().AuthorizeAsync(
            caller, tool, JsonSerializer.SerializeToElement(new { path = "Directory.Build.props", content = "x" }), CancellationToken.None);
}
