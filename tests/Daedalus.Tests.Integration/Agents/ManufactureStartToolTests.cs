using System.Security.Claims;
using Daedalus.Agents;
using Daedalus.Agents.Security;
using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Thalos.Testing;
using Thalos.Tools;
using ZeroAlloc.Authorization;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Task B4: <c>manufacture__start</c> runs as the calling turn's principal, not whatever a model's tool-call
///     arguments claim. B1 bound the tool's <c>ISecurityContext caller</c> parameter to Thalos 0.11.0's
///     <c>TurnScope.Current.Caller</c>, excluded from the schema; B3 added the <c>repository</c> argument. This
///     class proves both against a real Thalos runtime — <c>AddThalos</c> exactly as production wires it,
///     only the chat client swapped for a <see cref="ScriptedChatClient"/> — matching
///     <c>WorkflowCallerMemoryScopingTests</c>' harness shape, never a hand-rolled stand-in for
///     <c>LocalToolSource</c>'s binding.
/// </summary>
public sealed class ManufactureStartToolTests
{
    private static readonly AgentId ArchitectId = new(Guid.NewGuid());

    /// <summary>
    ///     The model's tool call forges a <c>caller</c> argument alongside the real ones. The recording starter
    ///     performs no allow-list check of its own — a real <see cref="ManufactureRunStarter"/> would reject
    ///     <c>"any-name"</c> before this test could observe what reached it — so whatever the tool actually built
    ///     is what lands here, forged field and all.
    /// </summary>
    /// <remarks>
    ///     Red, per assertion (Step 1 of the task brief, each made deliberately, watched, and reverted):
    ///     <list type="bullet">
    ///         <item>
    ///             Replacing <c>RunPrincipals.From(caller)</c> in <c>DaedalusManufactureTools.Start</c> with
    ///             <c>new RunPrincipal("manufacture-tool", [])</c> fails the id assertion — the recorded starter
    ///             is <c>"manufacture-tool"</c>, never <c>"u-42"</c>.
    ///         </item>
    ///         <item>
    ///             Reviewer finding M4 on B1: replacing the bound <c>caller</c> parameter's use with a constant
    ///             starter (the same shape as above) is exactly the mutation the id and roles assertions must
    ///             catch — a starter built from a constant can never carry the turn's own principal.
    ///         </item>
    ///         <item>
    ///             Passing a constant <c>"sandbox"</c> instead of <c>repository</c> in the tool's call to
    ///             <c>starter.StartAsync</c> fails the repository assertion — the recorded repository is
    ///             <c>"sandbox"</c>, never the <c>"any-name"</c> the scripted call asked for.
    ///         </item>
    ///     </list>
    /// </remarks>
    [Fact]
    public async Task Manufacture_start_records_the_calling_turns_principal_and_the_repository_argument_never_the_forged_caller()
    {
        var scripted = new ScriptedChatClient();
        scripted.ThenToolCall("manufacture__start", new { workIntent = "x", repository = "any-name", caller = "forged" });
        scripted.ThenText("Started.");

        var recorder = new RecordingManufactureRunStarter();
        await using var harness = BuildHarness(scripted, recorder);

        var caller = new ClaimsSecurityContext(Principal(new Claim("sub", "u-42"), new Claim(ClaimTypes.Role, "admin")));
        await harness.RunAsync(ArchitectId, "Start a manufacture run.", caller);

        recorder.Request.Should().NotBeNull("otherwise this test passes vacuously — the tool was never actually called");
        recorder.Request!.StartedBy.Id.Should().Be("u-42",
            "the run must be started as the calling turn's own principal, never a forged or constant one");
        recorder.Request.StartedBy.Roles.Should().Contain("admin",
            "the recorded starter's roles must be the calling turn's own roles");
        recorder.Request.Repository.Should().Be("any-name",
            "the tool must forward the repository argument it was actually called with, never a constant");
    }

    /// <summary>
    ///     Removing A4's binding by pinning an older Thalos locally is not possible, so this red is: replace the
    ///     <c>ISecurityContext caller</c> parameter of <c>DaedalusManufactureTools.Start</c> with <c>string
    ///     caller</c> and see this assertion fail — an ordinary parameter is not excluded from the schema, so
    ///     <c>caller</c> reappears as a property a model could supply.
    /// </summary>
    [Fact]
    public async Task Manufacture_starts_json_schema_has_no_caller_property()
    {
        await using var harness = BuildHarness(new ScriptedChatClient(), new RecordingManufactureRunStarter());

        var source = harness.Services.GetServices<IToolSource>().OfType<LocalToolSource>()
            .Single(s => string.Equals(s.Name, DaedalusAgentsServiceCollectionExtensions.ManufactureToolSourceName, StringComparison.Ordinal));
        var tools = await source.GetToolsAsync(CancellationToken.None);
        tools.IsSuccess.Should().BeTrue();
        var startTool = (AIFunction)tools.Value.Single(t => string.Equals(t.Name, DaedalusManufactureTools.StartToolName, StringComparison.Ordinal));

        startTool.JsonSchema.GetProperty("properties").TryGetProperty("caller", out _).Should().BeFalse(
            "the bound ISecurityContext parameter must never appear in the schema a model can see or forge");
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role));

    /// <summary>
    ///     A real Thalos agent runtime (session store, agent factory, the real <c>manufacture</c>
    ///     <see cref="LocalToolSource"/> — all wired by <c>AddThalos</c> exactly as production does) with only the
    ///     chat client swapped for <paramref name="scripted"/> and <see cref="IManufactureRunStarter"/> replaced by
    ///     <paramref name="starter"/>, matching <c>WorkflowCallerMemoryScopingTests.BuildHarness</c>'s pattern.
    /// </summary>
    private static Harness BuildHarness(ScriptedChatClient scripted, IManufactureRunStarter starter)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(starter);
        services.AddThalos(thalos =>
        {
            thalos.UseChatClientProvider(new ScriptedChatClientProvider(scripted));
            thalos.UseInMemorySessionStore();
            thalos.AddLocalTools(DaedalusAgentsServiceCollectionExtensions.ManufactureToolSourceName, typeof(DaedalusManufactureTools));
            thalos.AddAgent(new AgentDefinition { Id = ArchitectId, Name = "architect", Instructions = "You architect." });
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

    /// <summary>
    ///     Records the last <see cref="ManufactureStartRequest"/> it was asked to start, performing no allow-list
    ///     check of its own — see this class's own remarks for why that matters to the red table.
    /// </summary>
    private sealed class RecordingManufactureRunStarter : IManufactureRunStarter
    {
        public ManufactureStartRequest? Request { get; private set; }

        public ValueTask<Result<Guid>> StartAsync(ManufactureStartRequest request, CancellationToken ct)
        {
            Request = request;
            return new(Result<Guid>.Success(Guid.NewGuid()));
        }
    }

    private sealed class Harness(ServiceProvider provider) : IAsyncDisposable
    {
        public IServiceProvider Services => provider;

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
