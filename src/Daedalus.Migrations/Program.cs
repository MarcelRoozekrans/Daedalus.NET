using System.Data.Async.Adapters;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Migrations;
using Daedalus.Migrations;
using Daedalus.ServiceDefaults;
using Npgsql;
using ZeroAlloc.ORM;

var builder = Host.CreateApplicationBuilder(args);

// Add console logging (minimal for migration tool)
builder.Services.AddConsoleLogging();

// Add database context (key must match the Aspire database resource name)
builder.Services.AddApplicationDatabase(
    builder.Configuration,
    "daedalus",
    DatabaseSettings.GetDefaultConnectionString());

var host = builder.Build();

try
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    var connectionString = builder.Configuration.GetConnectionString("daedalus") ?? DatabaseSettings.GetDefaultConnectionString();

    // One connection for both steps, in one process: Daedalus' own migration source first, then the workflow
    // engine's. Daedalus' source adopts an EF Core database at its frozen head, builds a new one, and refuses any
    // other EF state, which exits 1 with the reason (Review Focus 5).
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    logger.LogInformation("Starting database migration");
    try
    {
        var daedalusApplied = await DaedalusMigrations.CreateRunner(connection.AsAsync()).RunAsync(CancellationToken.None);
        logger.DaedalusMigrationApplied(daedalusApplied.Count);
    }
    catch (ZeroAllocOrmMigrationConflictException ex)
    {
        logger.DaedalusMigrationRefused(ex.Message);
        Environment.Exit(1);
    }

    logger.LogInformation("Database migration completed successfully");

    // Phase 2.2 Part B: the workflow engine's raw-SQL migrations, applied as the same explicit, pre-boot deploy
    // step Daedalus' own migrations just ran as. AddWorkflowOrm's EnsureSchemaOnStartup is deliberately off (see
    // Daedalus.Agents.DaedalusAgentsServiceCollectionExtensions.AddDaedalusAgents) because migration 1004
    // (process_definition.content_hash, NOT NULL, no default) is not backward compatible with pre-1004 code:
    // applying it ahead of a rolling deploy would break process syncing with 23502 on every instance not yet
    // replaced. Running it here — the one place schema changes and this deploy happen as a single step —
    // avoids leaving that race to whichever host instance's EnsureSchemaOnStartup wins at boot.

    // Two outbox tables now live in this one database, written and polled by this one process: the channel
    // pipeline's "OutboxMessages" (quoted identifier, mixed case, created by the Daedalus baseline) for the
    // RepoDigest/channel pipeline, and ZeroAlloc.Outbox.Orm's OutboxMessages (unquoted in its own CREATE TABLE, so
    // PostgreSQL folds it to lower case: outboxmessages) for the workflow engine below. They differ only in identifier case and quoting, never collide —
    // PostgreSQL treats "OutboxMessages" and outboxmessages as distinct tables — and each is read by its own poller
    // (ZeroAlloc.Outbox's OutboxWorkerService for the quoted one, Daedalus.Agents.Workflow.WorkflowOutboxDispatchService
    // for the unquoted one). See tests/Daedalus.Tests.Integration/Migrations/WorkflowOrmMigrationTests.cs, which
    // applies both the Daedalus baseline and this file's raw-SQL ones to the same throwaway database and asserts
    // both tables exist side by side, rather than leaving this coexistence claim reasoned about instead of tested.
    logger.LogInformation("Starting workflow engine migration");

    // WorkflowEngineMigrator runs the outbox schema first: OrmWorkflowStore enqueues into it in the same
    // transaction as the workflow tables it writes, starting with the very first StartAsync call, so neither
    // table can be missing. That mirrors WorkflowOrmSchemaInitializer's own ordering in
    // Thalos.NET.Workflow.Orm, which this replaces with an explicit step rather than relying on
    // EnsureSchemaOnStartup at host boot. Before either source runs, it attributes the rows of a history table
    // from before ZeroAlloc.ORM scoped versions by source to the source that wrote each one: both sources
    // shared that table, and the runner's own upgrade refuses it from either. See PreScopingMigrationHistory.
    var outcome = await WorkflowEngineMigrator.ApplyAsync(connection);
    if (outcome.IsFailure)
    {
        logger.WorkflowEngineMigrationRefused(outcome.Error);
        Environment.Exit(1);
    }

    logger.WorkflowEngineMigrationApplied(outcome.Value.UpgradedHistoryRows, outcome.Value.Applied.Count);


    logger.LogInformation("Workflow engine migration completed successfully");
}
catch (Exception ex)
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "Database migration failed");
    Environment.Exit(1);
}

Environment.Exit(0);
