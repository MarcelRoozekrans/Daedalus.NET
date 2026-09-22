using System.Data.Async.Adapters;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Thalos;
using Thalos.Tools;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Task 10: the resume boundary. Three layers:
///     <list type="number">
///         <item>
///             Resume/cancel are REST endpoints (<c>WorkflowRunsController</c>), never a Thalos tool — see
///             <see cref="Resume_is_not_exposed_as_a_tool_on_any_registered_source"/>. This is the structural
///             half: it holds even if the authorization policy below were ever misconfigured.
///         </item>
///         <item>
///             Both endpoints require the <c>WorkflowResume</c> ASP.NET Core policy (<c>developer</c> or
///             <c>admin</c> role) — see <see cref="ResumeAuthorizationBoundaryTests"/>, which denies a caller
///             carrying the real, configured <c>DetachedRuns:Roles</c>.
///         </item>
///         <item>
///             A mismatched signal fails loudly, naming what the run actually awaits, rather than the silent
///             no-op <c>ZeroAlloc.Saga</c> shipped with — see <see cref="ResumeSignalMismatchTests"/>.
///         </item>
///     </list>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ResumeToolBoundaryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly IAgentRuntime _runtime = Substitute.For<IAgentRuntime>();
    private ApiWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _factory = new ApiWebApplicationFactory(fixture.ConnectionString, _runtime);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    ///     The structural half of the boundary: no local tool source — the only kind an agent's <c>Tools</c> list
    ///     can ever resolve to — exposes anything resume-capable. If an agent could resume its own approval gate,
    ///     every gate in the engine would be decorative regardless of what any authorization policy says.
    /// </summary>
    [Fact]
    public async Task Resume_is_not_exposed_as_a_tool_on_any_registered_source()
    {
        var toolNames = await ReadToolNamesAsync();

        toolNames.Should().NotBeEmpty("otherwise this test passes vacuously");
        toolNames.Should().NotContain(n => n.Contains("resume", StringComparison.OrdinalIgnoreCase),
            "resuming or cancelling a run must only be reachable through the authorized " +
            "POST /api/workflow-runs/{id}/resume|cancel endpoints, never as an agent-callable tool");
    }

    /// <summary>No tool source is even named <c>workflow</c> — resume has no source to be smuggled into.</summary>
    [Fact]
    public void No_tool_source_is_named_workflow()
    {
        var sourceNames = _factory.Services.GetServices<IToolSource>().OfType<LocalToolSource>().Select(s => s.Name).ToList();

        sourceNames.Should().NotBeEmpty("otherwise this test passes vacuously");
        sourceNames.Should().NotContain("workflow");
    }

    /// <summary>
    ///     Belt and suspenders on top of <see cref="Resume_is_not_exposed_as_a_tool_on_any_registered_source"/>:
    ///     even a configured agent whose <c>Tools</c> glob is broad enough to match everything else still cannot
    ///     resolve a resume-capable tool, because none exists to match.
    /// </summary>
    [Fact]
    public async Task No_configured_agents_tool_pattern_resolves_a_resume_capable_tool()
    {
        var toolNames = await ReadToolNamesAsync();
        var agents = _factory.Services.GetRequiredService<IAgentCatalog>().Agents;

        agents.Should().NotBeEmpty("otherwise this test passes vacuously");

        foreach (var agent in agents)
        {
            foreach (var pattern in agent.Tools)
            {
                toolNames.Where(t => Glob.IsMatch(pattern, t))
                    .Should().NotContain(t => t.Contains("resume", StringComparison.OrdinalIgnoreCase),
                        $"agent '{agent.Name}' pattern '{pattern}' must not resolve a resume-capable tool");
            }
        }
    }

    /// <summary>
    ///     Reads the in-process tool sources registered on the host, qualified the way the runtime does. MCP
    ///     sources are skipped deliberately — see <c>RepoToolBoundaryTests.ReadToolNamesAsync</c> for why.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadToolNamesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var names = new List<string>();

        foreach (var source in scope.ServiceProvider.GetServices<IToolSource>().OfType<LocalToolSource>())
        {
            var tools = await source.GetToolsAsync(CancellationToken.None);
            tools.IsSuccess.Should().BeTrue($"the '{source.Name}' tool source must enumerate");
            names.AddRange(tools.Value.Select(t => $"{source.Name}__{t.Name}"));
        }

        return names;
    }
}

/// <summary>
///     The authorization half of the boundary: <c>WorkflowRunsController</c>'s <c>WorkflowResume</c> policy,
///     exercised through the real, composed <c>Daedalus.Api</c> host rather than a hand-assembled fixture. ASP.NET
///     Core's authorization middleware evaluates <c>[Authorize]</c> from endpoint metadata before the MVC action
///     invoker constructs the controller, so a denial never needs <c>IWorkflowStore</c> to be registered — which
///     matters here because <see cref="ApiWebApplicationFactory"/> always sets <c>Thalos:Workflow:Enabled</c> to
///     <see langword="false"/> (see that class's own remarks).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ResumeAuthorizationBoundaryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly IAgentRuntime _runtime = Substitute.For<IAgentRuntime>();
    private ApiWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _factory = new ApiWebApplicationFactory(fixture.ConnectionString, _runtime);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Resume_requires_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{Guid.NewGuid()}/resume", new { signal = "human_approval", payload = (string?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    ///     <b>The one configuration line this whole layer rests on.</b> A detached run's roles come from
    ///     <c>DetachedRuns:Roles</c> — real, shipped configuration, not a role list typed into this test — so a
    ///     caller presenting exactly those roles is what a scheduled run's principal would look like if it ever
    ///     reached this endpoint. Phase 1.6 found a hand-built principal leaving a boundary test green while the
    ///     boundary itself was gone; reading the configured value instead means adding <c>developer</c> or
    ///     <c>admin</c> to that one line flips this test red — see the task report for the observed RED and
    ///     restored GREEN runs.
    /// </summary>
    [Fact]
    public async Task A_caller_with_the_configured_detached_run_roles_is_denied_resume()
    {
        var configured = _factory.Services.GetRequiredService<IOptions<DetachedRunOptions>>().Value;
        configured.Roles.Should().NotBeEmpty("otherwise this test passes vacuously");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, "schedule:daedalus");
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.RolesHeader, string.Join(',', configured.Roles));

        var response = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{Guid.NewGuid()}/resume", new { signal = "human_approval", payload = (string?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"DetachedRuns:Roles is [{string.Join(", ", configured.Roles)}] and must never grant 'developer' or " +
            "'admin' — that one line is what makes the resume/cancel endpoints deny an unattended run's shape " +
            "of caller at all");
    }

    /// <summary>Same denial, same configured roles, the other endpoint.</summary>
    [Fact]
    public async Task A_caller_with_the_configured_detached_run_roles_is_denied_cancel()
    {
        var configured = _factory.Services.GetRequiredService<IOptions<DetachedRunOptions>>().Value;
        configured.Roles.Should().NotBeEmpty("otherwise this test passes vacuously");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, "schedule:daedalus");
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.RolesHeader, string.Join(',', configured.Roles));

        var response = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{Guid.NewGuid()}/cancel", new { reason = "test" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>The other direction: a caller who actually carries the developer role is let through the policy.</summary>
    [Fact]
    public async Task A_developer_caller_passes_the_policy()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, "a-developer");
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.RolesHeader, "developer");

        var response = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{Guid.NewGuid()}/resume", new { signal = "human_approval", payload = (string?)null });

        // Pinned to exactly 500, not merely "not 401/403": NotBe on both would also pass on a 404 from a
        // missing or renamed route, which proves nothing about this policy. The controller's constructor
        // requires WorkflowRunGateway, never registered on this fixture because ApiWebApplicationFactory
        // always disables the workflow engine (see that class's own remarks) - so a request the policy lets
        // through fails DI resolution deterministically, and 500 is the one status that is only reachable
        // once authorization has already passed.
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}

/// <summary>
///     The failing-signal half of the boundary: a resume against a real, migrated Postgres-backed
///     <see cref="IWorkflowStore"/> must fail loudly and name what the run is actually awaiting, never silently
///     no-op it. <c>ZeroAlloc.Saga</c> shipped with exactly the opposite: an unexpected event type was dropped
///     with no error anywhere, leaving a saga stuck at its first step forever. Runs against a dedicated,
///     migrated scratch database, following <c>WorkflowOutboxDispatchEndToEndTests</c>'s pattern — not the shared
///     fixture database, which <see cref="ApiWebApplicationFactory"/> never runs Thalos.NET.Workflow.Orm's raw-SQL
///     migrations against.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ResumeSignalMismatchTests(PostgresFixture fixture)
{
    private const string ProcessName = "resume-boundary-test";

    private const string Yaml = """
        process: resume-boundary-test
        version: 1
        nodes:
          gate:
            await: human_approval
            next: finish
          finish:
            terminal: succeeded
        """;

    [Fact]
    public async Task Resuming_with_the_wrong_signal_fails_loudly_and_names_what_is_awaited()
    {
        await WithScratchDatabaseAsync(async store =>
        {
            var gateway = new WorkflowRunGateway(store.Store);
            var runId = await ParkedAtGateAsync(store, "c1");

            var result = await gateway.ResumeAsync(runId, "ci_passed", payload: null, CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.Error.Should().Contain("human_approval");
            (await store.Store.FindAsync(runId, CancellationToken.None))!.Status.Should().Be(WorkflowStatus.Awaiting);
        });
    }

    [Fact]
    public async Task Resuming_a_run_that_is_not_awaiting_anything_fails_loudly()
    {
        await WithScratchDatabaseAsync(async store =>
        {
            var gateway = new WorkflowRunGateway(store.Store);

            var result = await gateway.ResumeAsync(Guid.NewGuid(), "human_approval", payload: null, CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.Error.Should().Contain("was not found");
        });
    }

    /// <summary>The other side of the same boundary: a matching signal genuinely resumes the run.</summary>
    [Fact]
    public async Task Resuming_with_the_matching_signal_advances_the_run()
    {
        await WithScratchDatabaseAsync(async store =>
        {
            var gateway = new WorkflowRunGateway(store.Store);
            var runId = await ParkedAtGateAsync(store, "c2");

            var result = await gateway.ResumeAsync(runId, "human_approval", payload: null, CancellationToken.None);

            result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : string.Empty);
            var run = await store.Store.FindAsync(runId, CancellationToken.None);
            // Resuming moves the run off the gate onto "finish" at Running - it does not itself dispatch the
            // terminal node. Reaching Succeeded needs one more dispatch, which is WorkflowOutboxDispatchService's
            // job in production and not what this test is about; the assertion below is what ResumeAsync alone
            // is responsible for.
            run!.Status.Should().Be(WorkflowStatus.Running, "the gate's 'next' edge was resolved, but the terminal node it points to has not been dispatched yet");
            run.CurrentNode.Should().Be("finish");
        });
    }

    /// <summary>
    ///     Resume and cancel agree on 404 for a run that does not exist. Exercised through the real controller,
    ///     not just <see cref="WorkflowRunGateway"/> - <see cref="WorkflowRunGateway.ResumeAsync"/>'s own "not
    ///     found" failure alone maps to 409, which is why <see cref="WorkflowRunsController.Resume"/> checks
    ///     existence itself before delegating, the same way <see cref="WorkflowRunsController.Cancel"/> already
    ///     did.
    /// </summary>
    [Fact]
    public async Task The_controller_returns_not_found_for_resume_of_a_nonexistent_run()
    {
        await WithScratchDatabaseAsync(async store =>
        {
            var controller = new WorkflowRunsController(new WorkflowRunGateway(store.Store));

            var result = await controller.Resume(
                Guid.NewGuid(), new ResumeWorkflowRunRequest("human_approval", null), CancellationToken.None);

            result.Should().BeOfType<NotFoundResult>();
        });
    }

    /// <summary>Same 404, the other endpoint - see <see cref="The_controller_returns_not_found_for_resume_of_a_nonexistent_run"/>.</summary>
    [Fact]
    public async Task The_controller_returns_not_found_for_cancel_of_a_nonexistent_run()
    {
        await WithScratchDatabaseAsync(async store =>
        {
            var controller = new WorkflowRunsController(new WorkflowRunGateway(store.Store));

            var result = await controller.Cancel(Guid.NewGuid(), new CancelWorkflowRunRequest("test"), CancellationToken.None);

            result.Should().BeOfType<NotFoundResult>();
        });
    }

    /// <summary>Starts a run at the gate node and dispatches it once, parking it at <c>Awaiting</c>.</summary>
    private static async Task<Guid> ParkedAtGateAsync(ScratchStore store, string correlationKey)
    {
        var runId = await store.Store.StartAsync(ProcessName, 1, $"{correlationKey}:{Guid.NewGuid()}", "gate", CancellationToken.None);

        var run = await store.Store.FindAsync(runId, CancellationToken.None);
        var dispatcher = new WorkflowNodeDispatcher(
            store.Store,
            Substitute.For<ISubagentRunner>(),
            Substitute.For<IWorkflowReferenceResolver>(),
            store.Definitions,
            r => new WorkflowCaller(r));

        await dispatcher.DispatchAsync(new WorkflowDispatchMessage(runId, run!.CurrentSeq, "gate"), CancellationToken.None);

        var parked = await store.Store.FindAsync(runId, CancellationToken.None);
        parked!.Status.Should().Be(WorkflowStatus.Awaiting, "otherwise the rest of this test proves nothing");
        parked.AwaitingSignal.Should().Be("human_approval");

        return runId;
    }

    private async Task WithScratchDatabaseAsync(Func<ScratchStore, Task> body)
    {
        var dbName = $"resume_boundary_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;

        try
        {
            await MigrateAsync(connectionString);

            var options = new WorkflowOrmOptions { ConnectionString = connectionString };
            var definitions = new OrmProcessDefinitionStore(options);
            var store = new OrmWorkflowStore(options, definitions);

            var definition = ProcessLoader.Load(Yaml).Value;
            (await definitions.UpsertAndActivateAsync(definition, Yaml, CancellationToken.None)).IsSuccess.Should().BeTrue();

            await body(new ScratchStore(store, definitions));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
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

    private sealed record ScratchStore(OrmWorkflowStore Store, OrmProcessDefinitionStore Definitions);
}
