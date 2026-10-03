using System.Data.Async.Adapters;
using System.Text.Json;
using System.Text.Json.Nodes;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Thalos;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     A throwaway, fully migrated database; a <see cref="LocalGitRemote"/> allow-listed as <see cref="Repository"/>;
///     a temp <c>DataRoot</c>; and the real Api booted over them with the workflow engine on. The database set-up is
///     the one <c>StartRunEndpointTests</c> used to carry itself: EF Core migrations, the <c>vector</c> extension,
///     then Thalos's outbox and workflow raw-SQL migrations. Disposing drops the database <c>WITH (FORCE)</c> and
///     deletes the remote and the <c>DataRoot</c>.
/// </summary>
/// <remarks>
///     <para>
///     The repository settings are applied before the caller's <c>settings</c>, so a test can override any of them.
///     Only <c>Name</c>, <c>Remote</c> and <c>DefaultBranch</c> of entry 0 are set: configuration merges by index, so
///     the shipped entry's <c>Solution</c>, <c>Sandbox.sln</c>, stays. In local mode nothing loads it, because no
///     run-scoped MCP server is configured; in sandbox mode the run's Roslyn server loads it inside the run's container.
///     </para>
///     <para>
///     <b>Local mode</b>, the default: <c>Thalos:McpConfigPath</c> points at <c>no-run-scoped.mcp.json</c> next to the test
///     assembly, an absolute path because a relative one resolves against <c>src/Daedalus.Api</c>. With no run-scoped
///     server declared, no test host starts a real Roslyn server (ruling R24).
///     </para>
///     <para>
///     <b>Sandbox mode</b>, phase 2.6 task B8, when <c>sandboxImage</c> is given: the sandbox is enabled over that image,
///     on a Docker network of the host's own, so it gets its own gateway and egress containers (ruling R45), and the
///     gateway takes a free loopback port. <c>Thalos:McpConfigPath</c> is the shipped <c>.mcp.json</c>'s <c>roslyn</c>
///     entry, <c>runScoped</c> block, pinned package and <c>dnx</c> command included, which the host makes remote, so a
///     run's Roslyn calls go to the server inside the run's sandbox. Two changes, both outside what is under test, as
///     <c>RunScopedRoslynRoutingTests</c> makes the first: the host-wide server, which serves only callers with no run and
///     supplies the remote entry's tool schemas, loads a one-project solution in a temp directory instead of the
///     developer's own <c>Daedalus.sln</c>; and the <c>context7</c> entry is left out, because it is a public web service
///     an agent build would otherwise have to reach.
///     </para>
/// </remarks>
internal sealed class ScratchWorkflowHost : IAsyncDisposable
{
    /// <summary>The allow-listed name <see cref="Remote"/> is registered under.</summary>
    public const string Repository = "sandbox";

    /// <summary>How long <see cref="WaitForAsync"/> polls before it fails, unless the caller passes its own bound.</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(90);

    private readonly PostgresFixture _fixture;
    private readonly string _databaseName;
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly IAgentRuntime? _runtime;
    private readonly bool? _squadEnabled;
    private readonly string? _hostToolsRoot;

    private ScratchWorkflowHost(
        PostgresFixture fixture, string databaseName, string connectionString, LocalGitRemote remote, string dataRoot,
        ApiWebApplicationFactory factory, IReadOnlyDictionary<string, string?> settings, IAgentRuntime? runtime, bool? squadEnabled,
        string? hostToolsRoot, string? sandboxNetwork)
    {
        _fixture = fixture;
        _databaseName = databaseName;
        ConnectionString = connectionString;
        Remote = remote;
        DataRoot = dataRoot;
        Factory = factory;
        _settings = settings;
        _runtime = runtime;
        _squadEnabled = squadEnabled;
        _hostToolsRoot = hostToolsRoot;
        SandboxNetwork = sandboxNetwork;
    }

    /// <summary>The running Api. <see cref="RestartAsync"/> replaces it with a new one over the same database and <c>DataRoot</c>.</summary>
    public ApiWebApplicationFactory Factory { get; private set; }

    public string ConnectionString { get; }

    public LocalGitRemote Remote { get; }

    public string DataRoot { get; }

    /// <summary>The sandbox network this host's runs join, or null in local mode.</summary>
    public string? SandboxNetwork { get; }

    /// <summary>
    ///     Sandbox mode only: the temp directory holding the host's MCP configuration and the host-wide Roslyn server's
    ///     one-project solution, the one place on this host where that server evaluates MSBuild. Null in local mode.
    /// </summary>
    public string? HostToolsRoot => _hostToolsRoot;

    /// <summary>Where the workspace provider puts each run's worktree, as <c>&lt;RunsRoot&gt;/&lt;run-id&gt;</c>.</summary>
    public string RunsRoot => Path.Combine(DataRoot, "runs");

    public IWorkflowStore Store => Factory.Services.GetRequiredService<IWorkflowStore>();

    /// <summary>
    ///     Boots a host. <paramref name="seed"/> is the remote's <c>main</c>, on top of <c>README.md</c>; left out, it
    ///     is <c>AGENT.md</c> holding <c>Run dotnet test.</c> plus a final newline, which is there because Thalos's base-file reader adds one (Thalos issue #245), and <c>src/A.cs</c> holding <c>class A {}</c>.
    ///     <paramref name="runtime"/>, <paramref name="squadEnabled"/> and <paramref name="configureServices"/> are passed
    ///     to <see cref="ApiWebApplicationFactory"/> as they are, so a null <paramref name="runtime"/> keeps the host's
    ///     own <c>ThalosAgentRuntime</c>. <paramref name="sandboxImage"/>, when given, boots the host in sandbox mode over
    ///     that image, on <paramref name="sandboxNetwork"/>, which is then required: a network the caller names is one its
    ///     clean-up covers, so no host makes Docker objects outside it. See the type's remarks.
    /// </summary>
    public static async Task<ScratchWorkflowHost> StartAsync(
        PostgresFixture fixture,
        IAgentRuntime? runtime,
        IReadOnlyList<(string Path, string Content)>? seed = null,
        IReadOnlyDictionary<string, string?>? settings = null,
        bool? squadEnabled = null,
        Action<IServiceCollection>? configureServices = null,
        string? sandboxImage = null,
        string? sandboxNetwork = null)
    {
        if (sandboxImage is not null && string.IsNullOrWhiteSpace(sandboxNetwork))
        {
            throw new ArgumentException("A sandbox-mode host needs the sandbox network its caller cleans up.", nameof(sandboxNetwork));
        }

        var databaseName = $"scratch_workflow_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync(fixture, $"CREATE DATABASE \"{databaseName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;

        LocalGitRemote? remote = null;
        string? dataRoot = null;
        string? hostToolsRoot = null;
        ApiWebApplicationFactory? factory = null;
        try
        {
            await MigrateAsync(connectionString);

            // The AGENT.md final newline is there because Thalos's base-file reader adds one (Thalos issue #245); the
            // no-final-newline and CRLF cases stay untested until #245 is fixed.
            remote = LocalGitRemote.Create([.. seed ?? [("AGENT.md", "Run dotnet test.\n"), ("src/A.cs", "class A {}")]]);
            dataRoot = Directory.CreateTempSubdirectory("daedalus-workflow-data-").FullName;

            var all = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Thalos:Workflow:Repositories:0:Name"] = Repository,
                ["Thalos:Workflow:Repositories:0:Remote"] = remote.Url,
                ["Thalos:Workflow:Repositories:0:DefaultBranch"] = "main",
                ["Thalos:Workflow:DataRoot"] = dataRoot,
                ["Thalos:Workflow:CommitAuthor:Name"] = "Daedalus Test",
                ["Thalos:Workflow:CommitAuthor:Email"] = "daedalus-test@example.invalid",
                ["Thalos:McpConfigPath"] = Path.Combine(AppContext.BaseDirectory, "no-run-scoped.mcp.json"),
                // Local mode: these suites test the workflow, not the container. ApiWebApplicationFactory defaults to it
                // too, and gives the implement grant the extension list local mode requires; stated here as well
                // because this host is the one that runs the engine.
                ["Thalos:Workflow:Sandbox:Enabled"] = "false",
            };

            if (sandboxImage is not null)
            {
                hostToolsRoot = Directory.CreateTempSubdirectory("daedalus-sandbox-host-").FullName;
                all["Thalos:Workflow:Sandbox:Enabled"] = "true";
                all["Thalos:Workflow:Sandbox:Image"] = sandboxImage;
                all["Thalos:Workflow:Sandbox:Docker:Network"] = sandboxNetwork;
                all["Thalos:Workflow:Sandbox:Docker:GatewayPort"] = "0";
                all["Thalos:McpConfigPath"] = await WriteSandboxMcpConfigAsync(hostToolsRoot);
            }
            else
            {
                sandboxNetwork = null;
            }

            foreach (var (key, value) in settings ?? new Dictionary<string, string?>(StringComparer.Ordinal))
            {
                all[key] = value;
            }

            factory = Boot(connectionString, runtime, squadEnabled, configureServices, all);
            return new ScratchWorkflowHost(
                fixture, databaseName, connectionString, remote, dataRoot, factory, all, runtime, squadEnabled, hostToolsRoot, sandboxNetwork);
        }
        catch
        {
            await TearDownAsync(fixture, databaseName, remote, dataRoot, hostToolsRoot, factory);
            throw;
        }
    }

    /// <summary>
    ///     Stops this host's Api and boots a new one over the same database, remote, <c>DataRoot</c> and settings, the
    ///     sandbox network included, as a restarted deployment would. <paramref name="configureServices"/> replaces the
    ///     first boot's, so a test can hand the new host a new model script.
    /// </summary>
    public async Task RestartAsync(Action<IServiceCollection>? configureServices)
    {
        await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        Factory = Boot(ConnectionString, _runtime, _squadEnabled, configureServices, _settings);
    }

    /// <summary>A client authenticated through <see cref="HeaderTestAuthHandler"/>: <paramref name="user"/> becomes the <c>sub</c> claim.</summary>
    public HttpClient Client(string user, params string[] roles)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.RolesHeader, string.Join(',', roles));
        return client;
    }

    /// <summary>
    ///     Polls run <paramref name="runId"/> until <paramref name="until"/> holds, the run fails or is cancelled, or
    ///     <paramref name="timeout"/>, 90 seconds when left out, passes. The assertion names where the run stopped, so a
    ///     timeout says which node it stuck on and why. The bound is a hang guard: nothing asserts how long a run took.
    /// </summary>
    public async Task<WorkflowRun> WaitForAsync(Guid runId, Func<WorkflowRun, bool> until, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? WaitTimeout);
        WorkflowRun? run;
        do
        {
            run = await Store.FindAsync(runId, CancellationToken.None);
            if (run is not null && (until(run) || run.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        while (DateTime.UtcNow < deadline);

        run.Should().NotBeNull($"run {runId} should exist");
        until(run!).Should().BeTrue(
            $"the run should have {what}, but stopped with status {run!.Status} at '{run.CurrentNode}', last error: {run.LastError}");
        return run;
    }

    /// <summary>The host-written records of run <paramref name="runId"/> of <paramref name="kind"/>, in append order, as the run view reads them.</summary>
    public async Task<IReadOnlyList<WorkflowRunRecord>> RecordsAsync(Guid runId, string kind) =>
        await Factory.Services.GetRequiredService<WorkflowRunGateway>().ListRecordsAsync(runId, kind, CancellationToken.None);

    /// <summary>The run directories under <see cref="RunsRoot"/>, or none when it does not exist yet.</summary>
    public IReadOnlyList<string> RunDirectories() =>
        Directory.Exists(RunsRoot) ? Directory.GetDirectories(RunsRoot) : [];

    public ValueTask DisposeAsync() => new(TearDownAsync(_fixture, _databaseName, Remote, DataRoot, _hostToolsRoot, Factory));

    private static ApiWebApplicationFactory Boot(
        string connectionString, IAgentRuntime? runtime, bool? squadEnabled, Action<IServiceCollection>? configureServices,
        IReadOnlyDictionary<string, string?> settings)
    {
        var factory = new ApiWebApplicationFactory(
            connectionString, runtime, workflowEnabled: true, squadEnabled: squadEnabled, configureServices: configureServices, settings: settings);

        // Builds and starts the host now, so the process and skill syncs, which each await their first pass inside
        // StartAsync, have run before any request, and before a test deactivates a synced skill.
        _ = factory.Services;
        return factory;
    }

    /// <summary>
    ///     Writes the sandbox-mode MCP configuration under <paramref name="root"/> and returns its path: the shipped
    ///     <c>.mcp.json</c>'s <c>roslyn</c> entry as it is, but for its last argument, the host-wide server's solution, which
    ///     becomes a one-project solution written next to it. See the type's remarks.
    /// </summary>
    private static async Task<string> WriteSandboxMcpConfigAsync(string root)
    {
        var shipped = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Daedalus.Api.mcp.json")),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        var roslyn = shipped["mcpServers"]!["roslyn"]!.DeepClone();
        roslyn["runScoped"].Should().NotBeNull("the shipped roslyn entry must be run-scoped, or sandbox mode has no remote server to make of it");
        var args = roslyn["args"]!.AsArray();

        var solution = Path.Combine(root, "host", "HostApp.sln");
        Directory.CreateDirectory(Path.Combine(root, "host", "Lib"));
        await File.WriteAllTextAsync(solution, """

            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib\Lib.csproj", "{6E2C5B1A-4E0B-4C57-9C3E-2F1D6A8B7C11}"
            EndProject
            Global
            EndGlobal
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "host", "Lib", "Lib.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        await File.WriteAllTextAsync(Path.Combine(root, "host", "Lib", "Class1.cs"), "namespace Lib; public class Class1 { }");
        args[^1] = solution.Replace('\\', '/');

        var config = new JsonObject { ["mcpServers"] = new JsonObject { ["roslyn"] = roslyn } };
        var path = Path.Combine(root, "sandbox.mcp.json");
        await File.WriteAllTextAsync(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static async Task TearDownAsync(
        PostgresFixture fixture, string databaseName, LocalGitRemote? remote, string? dataRoot, string? hostToolsRoot,
        ApiWebApplicationFactory? factory)
    {
        try
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync(fixture, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
            remote?.Dispose();
            if (dataRoot is not null)
            {
                LocalGitRemote.DeleteReadOnly(dataRoot);
            }

            if (hostToolsRoot is not null)
            {
                LocalGitRemote.DeleteReadOnly(hostToolsRoot);
            }
        }
    }

    private static async Task MigrateAsync(string connectionString)
    {
        await using (var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString)))
        {
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector");
            await db.Database.MigrateAsync();
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var asyncConnection = connection.AsAsync();
        var dialect = new PostgresMigrationDialect();
        await new MigrationRunner(asyncConnection, OutboxOrmMigrations.Postgres, dialect).RunAsync();
        await new MigrationRunner(asyncConnection, WorkflowOrmMigrations.Postgres, dialect).RunAsync();
    }

    private static async Task ExecuteOnServerAsync(PostgresFixture fixture, string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
