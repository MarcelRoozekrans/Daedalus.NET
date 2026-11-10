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

var host = builder.Build();

try
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    // The key must match the Aspire database resource name.
    var connectionString = builder.Configuration.GetConnectionString("daedalus") ?? DatabaseSettings.GetDefaultConnectionString();

    // One connection for both steps, in one process: Daedalus' own migration source first, then the workflow
    // engine's. Daedalus' source adopts an EF Core database at its frozen head, builds a new one, and refuses any
    // other EF state, which exits 1 with the reason (Review Focus 5).
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    // The history table first, before any runner. A table from before ZeroAlloc.ORM 2.2.0 scoped versions by
    // source holds the Daedalus, outbox and workflow rows unscoped, and a runner upgrading it assigns every row to
    // its own source and refuses the rest: the Daedalus runner would refuse an EF database that carries the
    // outbox and workflow rows of an earlier Daedalus.Migrations run with a misleading cause. See
    // PreScopingMigrationHistory.
    var upgrade = await PreScopingMigrationHistory.UpgradeAsync(connection, WorkflowEngineMigrator.Sources);
    if (upgrade.IsFailure)
    {
        logger.WorkflowEngineMigrationRefused(upgrade.Error);
        Environment.Exit(1);
    }

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

    // Two outbox tables live in this one database, both on ZeroAlloc.Outbox.Orm's store: the channel
    // pipeline's "OutboxMessages" (quoted identifier, mixed case) for the RepoDigest/channel messages, and
    // ZeroAlloc.Outbox.Orm's own OutboxMessages (unquoted in its CREATE TABLE, so PostgreSQL folds it to lower
    // case: outboxmessages) for the workflow engine below. They differ only in identifier case and quoting,
    // never collide — PostgreSQL treats "OutboxMessages" and outboxmessages as distinct tables — and each is
    // read by its own poller: the channel pipeline's OutboxWorkerService for the quoted one, with
    // TableName "OutboxMessages" quoted and QuoteColumns on, and
    // Daedalus.Agents.Workflow.WorkflowOutboxDispatchService for the unquoted one.
    // Only the unquoted table comes from OutboxOrmMigrations, run below. The quoted one is the table EF Core
    // created and the Daedalus baseline migration above now owns, lease columns included, so it needs no
    // outbox migration of its own; OutboxOrmMigrations.For could not create it anyway, as it takes a plain
    // identifier only. See tests/Daedalus.Tests.Integration/Migrations/WorkflowOrmMigrationTests.cs, which
    // applies both the Daedalus baseline and this file's raw-SQL ones to the same throwaway database and
    // asserts both tables exist side by side, and Channels/ChannelOutboxCarryOverTests, which drive both.
    logger.LogInformation("Starting workflow engine migration");

    // WorkflowEngineMigrator runs the outbox schema first: OrmWorkflowStore enqueues into it in the same
    // transaction as the workflow tables it writes, starting with the very first StartAsync call, so neither
    // table can be missing. That mirrors WorkflowOrmSchemaInitializer's own ordering in
    // Thalos.NET.Workflow.Orm, which this replaces with an explicit step rather than relying on
    // EnsureSchemaOnStartup at host boot.
    var applied = await WorkflowEngineMigrator.ApplyAsync(connection);
    logger.WorkflowEngineMigrationApplied(upgrade.Value, applied.Count);

    logger.LogInformation("Workflow engine migration completed successfully");
}
catch (Exception ex)
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "Database migration failed");
    Environment.Exit(1);
}

Environment.Exit(0);
