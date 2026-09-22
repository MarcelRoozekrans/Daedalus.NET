using System.Data.Async.Adapters;
using Daedalus.Infrastructure.Persistence;
using Daedalus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;

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
    using var scope = host.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Starting database migration");
    await dbContext.Database.MigrateAsync();
    logger.LogInformation("Database migration completed successfully");

    // Phase 2.2 Part B: the workflow engine's raw-SQL migrations, applied as the same explicit, pre-boot deploy
    // step EF Core's migrations just ran as. AddWorkflowOrm's EnsureSchemaOnStartup is deliberately off (see
    // Daedalus.Agents.DaedalusAgentsServiceCollectionExtensions.AddDaedalusAgents) because migration 1004
    // (process_definition.content_hash, NOT NULL, no default) is not backward compatible with pre-1004 code:
    // applying it ahead of a rolling deploy would break process syncing with 23502 on every instance not yet
    // replaced. Running it here — the one place schema changes and this deploy happen as a single step —
    // avoids leaving that race to whichever host instance's EnsureSchemaOnStartup wins at boot.

    // Two outbox tables now live in this one database, written and polled by this one process: EF Core's
    // "OutboxMessages" (quoted identifier, mixed case, created by ApplicationDbContext's own migrations above —
    // AddOutboxMessages()/OutboxMessageEntity) for the RepoDigest/channel pipeline, and ZeroAlloc.Outbox.Orm's
    // OutboxMessages (unquoted in its own CREATE TABLE, so PostgreSQL folds it to lower case: outboxmessages) for
    // the workflow engine below. They differ only in identifier case and quoting, never collide — PostgreSQL
    // treats "OutboxMessages" and outboxmessages as distinct tables — and each is read by its own poller
    // (ZeroAlloc.Outbox's OutboxWorkerService for the quoted one, Daedalus.Agents.Workflow.WorkflowOutboxDispatchService
    // for the unquoted one). See tests/Daedalus.Tests.Integration/Migrations/WorkflowOrmMigrationTests.cs, which
    // applies both EF Core's migrations and this file's raw-SQL ones to the same throwaway database and asserts
    // both tables exist side by side, rather than leaving this coexistence claim reasoned about instead of tested.
    var connectionString = builder.Configuration.GetConnectionString("daedalus") ?? DatabaseSettings.GetDefaultConnectionString();
    logger.LogInformation("Starting workflow engine migration");
    await using (var workflowConnection = new NpgsqlConnection(connectionString))
    {
        await workflowConnection.OpenAsync();
        var asyncConnection = workflowConnection.AsAsync();
        var dialect = new PostgresMigrationDialect();

        // Outbox schema first: OrmWorkflowStore enqueues into it in the same transaction as the workflow tables
        // it writes, starting with the very first StartAsync call, so neither table can be missing — mirrors
        // WorkflowOrmSchemaInitializer's own ordering (Thalos.NET.Workflow.Orm), which this replaces with an
        // explicit step rather than relying on EnsureSchemaOnStartup at host boot.
        await new MigrationRunner(asyncConnection, OutboxOrmMigrations.Postgres, dialect).RunAsync();
        await new MigrationRunner(asyncConnection, WorkflowOrmMigrations.Postgres, dialect).RunAsync();
    }

    logger.LogInformation("Workflow engine migration completed successfully");
}
catch (Exception ex)
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "Database migration failed");
    Environment.Exit(1);
}

Environment.Exit(0);
