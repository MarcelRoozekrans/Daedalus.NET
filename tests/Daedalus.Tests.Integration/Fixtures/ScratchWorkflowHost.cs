using System.Data.Async.Adapters;
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
///     the shipped entry's <c>Solution</c>, <c>Sandbox.sln</c>, stays. Nothing loads it, because no run-scoped MCP
///     server is configured.
///     </para>
///     <para>
///     <c>Thalos:McpConfigPath</c> points at <c>no-run-scoped.mcp.json</c> next to the test assembly, an absolute path
///     because a relative one resolves against <c>src/Daedalus.Api</c>. With no run-scoped server declared, no test
///     host starts a real Roslyn server (ruling R24).
///     </para>
/// </remarks>
internal sealed class ScratchWorkflowHost : IAsyncDisposable
{
    /// <summary>The allow-listed name <see cref="Remote"/> is registered under.</summary>
    public const string Repository = "sandbox";

    /// <summary>How long <see cref="WaitForAsync"/> polls before it fails.</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(90);

    private readonly PostgresFixture _fixture;
    private readonly string _databaseName;

    private ScratchWorkflowHost(
        PostgresFixture fixture, string databaseName, string connectionString, LocalGitRemote remote, string dataRoot,
        ApiWebApplicationFactory factory)
    {
        _fixture = fixture;
        _databaseName = databaseName;
        ConnectionString = connectionString;
        Remote = remote;
        DataRoot = dataRoot;
        Factory = factory;
    }

    public ApiWebApplicationFactory Factory { get; }

    public string ConnectionString { get; }

    public LocalGitRemote Remote { get; }

    public string DataRoot { get; }

    /// <summary>Where the workspace provider puts each run's worktree, as <c>&lt;RunsRoot&gt;/&lt;run-id&gt;</c>.</summary>
    public string RunsRoot => Path.Combine(DataRoot, "runs");

    public IWorkflowStore Store => Factory.Services.GetRequiredService<IWorkflowStore>();

    /// <summary>
    ///     Boots a host. <paramref name="seed"/> is the remote's <c>main</c>, on top of <c>README.md</c>; left out, it
    ///     is <c>AGENT.md</c> holding <c>Run dotnet test.</c> and <c>src/A.cs</c> holding <c>class A {}</c>.
    ///     <paramref name="runtime"/>, <paramref name="squadEnabled"/> and <paramref name="configureServices"/> are passed
    ///     to <see cref="ApiWebApplicationFactory"/> as they are, so a null <paramref name="runtime"/> keeps the host's
    ///     own <c>ThalosAgentRuntime</c>.
    /// </summary>
    public static async Task<ScratchWorkflowHost> StartAsync(
        PostgresFixture fixture,
        IAgentRuntime? runtime,
        IReadOnlyList<(string Path, string Content)>? seed = null,
        IReadOnlyDictionary<string, string?>? settings = null,
        bool? squadEnabled = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var databaseName = $"scratch_workflow_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync(fixture, $"CREATE DATABASE \"{databaseName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;

        LocalGitRemote? remote = null;
        string? dataRoot = null;
        ApiWebApplicationFactory? factory = null;
        try
        {
            await MigrateAsync(connectionString);

            remote = LocalGitRemote.Create([.. seed ?? [("AGENT.md", "Run dotnet test."), ("src/A.cs", "class A {}")]]);
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
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>(StringComparer.Ordinal))
            {
                all[key] = value;
            }

            factory = new ApiWebApplicationFactory(
                connectionString, runtime, workflowEnabled: true, squadEnabled: squadEnabled, configureServices: configureServices, settings: all);

            // Builds and starts the host now, so the process and skill syncs, which each await their first pass inside
            // StartAsync, have run before any request, and before a test deactivates a synced skill.
            _ = factory.Services;

            return new ScratchWorkflowHost(fixture, databaseName, connectionString, remote, dataRoot, factory);
        }
        catch
        {
            await TearDownAsync(fixture, databaseName, remote, dataRoot, factory);
            throw;
        }
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
    ///     90 seconds pass. The assertion names where the run stopped, so a timeout says which node it stuck on and why.
    /// </summary>
    public async Task<WorkflowRun> WaitForAsync(Guid runId, Func<WorkflowRun, bool> until, string what)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
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

    public ValueTask DisposeAsync() => new(TearDownAsync(_fixture, _databaseName, Remote, DataRoot, Factory));

    private static async Task TearDownAsync(
        PostgresFixture fixture, string databaseName, LocalGitRemote? remote, string? dataRoot, ApiWebApplicationFactory? factory)
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
