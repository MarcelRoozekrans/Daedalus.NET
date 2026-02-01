# Ralph Loop Console Application

Distributed Ralph loop implementation for .NET 10 with PostgreSQL persistence and GitHub Copilot SDK integration.

## Project Structure

```
RalphLoopConsole/
├── Domain/
│   └── Entities/
│       ├── Task.cs                    # Ralph loop task aggregate
│       ├── TaskExecution.cs           # Single iteration record
│       ├── ExecutionSession.cs        # Worker session tracking
│       └── TaskStatus.cs              # Task lifecycle status
├── Application/
│   ├── Abstractions/
│   │   ├── ILlmService.cs             # LLM abstraction
│   │   ├── ITaskRepository.cs         # Task data access
│   │   └── IExecutionSessionRepository.cs  # Session data access
│   └── Services/
│       ├── RalphLoopService.cs        # Core loop orchestration
│       └── TaskAssignmentService.cs   # Distributed task claiming
├── Infrastructure/
│   ├── Persistence/
│   │   ├── ApplicationDbContext.cs    # EF Core context
│   │   ├── TaskRepository.cs          # Task repository
│   │   └── ExecutionSessionRepository.cs
│   └── Llm/
│       └── CopilotLlmService.cs       # GitHub Copilot integration
└── ConsoleApp/
    ├── Program.cs                      # Startup & DI configuration
    ├── RalphLoopWorker.cs             # Main worker service
    ├── appsettings.json               # Configuration
    └── appsettings.Development.json
```

## Architecture

### Distributed Task Processing

```
Multiple Workers (Concurrent)
    ↓
Atomic Task Claiming (SELECT FOR UPDATE SKIP LOCKED)
    ↓
Ralph Loop Execution (LLM Iterations)
    ↓
PostgreSQL Persistence (Task Backlog)
    ↓
Heartbeat Monitoring & Stale Task Reclamation
```

### Key Features

✅ **Distributed Locking**: Prevents race conditions with database-level SELECT FOR UPDATE SKIP LOCKED  
✅ **Session Heartbeats**: Health monitoring with configurable staleness timeout (5 minutes)  
✅ **Task Reclamation**: Automatically recovers tasks from crashed workers  
✅ **Railway-Oriented Programming**: Result<T> for error handling (CSharpFunctionalExtensions)  
✅ **Structured Logging**: Serilog with file rotation  
✅ **EF Core Pooling**: DbContext pooling for performance  
✅ **Concurrent Iterations**: Multiple workers processing same backlog simultaneously

## Database Setup

### Prerequisites

- PostgreSQL 13+
- dotnet 10 CLI

### Initial Database

```bash
# Create database
createdb daedalus

# Run migrations
cd src/RalphLoopConsole
dotnet ef database update --startup-project ConsoleApp
```

### Connection String

Default: `Host=localhost;Port=5432;Database=daedalus;Username=postgres;Password=postgres`

Override via environment variable:

```bash
export ConnectionStrings__DefaultConnection="Host=your-host;Port=5432;Database=daedalus;..."
```

## Configuration

### Environment Variables

```bash
# GitHub Copilot CLI should be installed and authenticated
# No additional env variables required

# Optional: Override database connection
export ConnectionStrings__DefaultConnection="..."

# Optional: Worker name
export WORKER_NAME="worker-001"
```

### appsettings.json

```json
{
    "ConnectionStrings": {
        "DefaultConnection": "Host=localhost;..."
    },
    "Copilot": {
        "Model": "gpt-4",
        "CliPath": null,
        "LogLevel": "warning"
    }
}
```

### GitHub Copilot SDK Setup

The application uses the official **GitHub Copilot SDK** for .NET. To use it:

1. **Install GitHub Copilot CLI**:

    ```bash
    npm install -g @github/copilot@latest
    ```

2. **Authenticate with GitHub**:

    ```bash
    copilot auth login
    ```

3. **Verify Installation**:
    ```bash
    copilot --version
    ```

The SDK automatically manages the CLI process lifecycle, including starting and stopping the Copilot process.

## Running the Application

### Single Worker

```bash
dotnet run --project src/RalphLoopConsole/ConsoleApp
```

### Multiple Concurrent Workers

```bash
# Terminal 1
dotnet run --project src/RalphLoopConsole/ConsoleApp

# Terminal 2
dotnet run --project src/RalphLoopConsole/ConsoleApp

# Terminal 3
dotnet run --project src/RalphLoopConsole/ConsoleApp
```

All workers will:

- Poll for pending tasks every 5 seconds
- Atomically claim available tasks
- Execute Ralph loops until completion or max iterations
- Send heartbeats every 30 seconds
- Check for and reclaim stale tasks every 5 minutes

### With Custom Configuration

```bash
# Override connection string
dotnet run --project src/RalphLoopConsole/ConsoleApp \
  -- --ConnectionStrings:DefaultConnection="Host=prod-db;..."
```

## Creating Tasks

Tasks can be created programmatically or via SQL:

```csharp
// Programmatic
var taskResult = Task.Create(
    id: Guid.NewGuid(),
    prompt: "Your prompt here. Output <promise>COMPLETE</promise> when done.",
    completionPromise: "<promise>COMPLETE</promise>",
    maxIterations: 30
);

if (taskResult.IsSuccess)
{
    await taskRepository.AddAsync(taskResult.Value, ct);
}
```

```sql
-- SQL
INSERT INTO "Tasks" ("Id", "Prompt", "CompletionPromise", "MaxIterations", "Status", "CreatedAt")
VALUES
  (
    '550e8400-e29b-41d4-a716-446655440000',
    'Implement a fibonacci function. Output <promise>COMPLETE</promise>',
    '<promise>COMPLETE</promise>',
    20,
    0,
    now()
  );
```

## Monitoring

### Check Active Sessions

```sql
SELECT * FROM "ExecutionSessions" WHERE "IsActive" = true;
```

### Check Task Progress

```sql
SELECT
  "Id", "Status", "IterationCount", "MaxIterations",
  "CurrentSessionId", "CreatedAt", "CompletedAt"
FROM "Tasks"
ORDER BY "CreatedAt" DESC;
```

### Check Task Executions

```sql
SELECT
  "TaskId", "IterationNumber", "CompletionPromiseFound",
  "ExecutionDuration", "ExecutedAt"
FROM "TaskExecutions"
WHERE "TaskId" = 'your-task-id'
ORDER BY "IterationNumber";
```

### View Logs

```bash
# Real-time logs
tail -f logs/ralph-loop-*.txt

# Recent errors
grep ERROR logs/ralph-loop-*.txt
```

## Performance Tuning

### Database Indexes

Already configured for:

- `Tasks.Status` - Fast pending task queries
- `Tasks.CurrentSessionId` - Fast session task lookup
- `Tasks.CreatedAt` - Chronological ordering
- `TaskExecutions.TaskId` - Execution history lookup
- `ExecutionSessions.IsActive` - Active session filtering

### Connection Pooling

DbContext pooling is enabled automatically:

```csharp
services.AddDbContextPool<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString)
);
```

### Async Best Practices

- All I/O operations use `async/await`
- No blocking calls (`.Result`, `.Wait()`, etc.)
- Proper `CancellationToken` propagation throughout

## Error Handling

All operations use Railway-Oriented Programming with `Result<T>`:

```csharp
var result = await service.SomeOperationAsync(ct);

if (result.IsFailure)
{
    _logger.LogError("Operation failed: {Error}", result.Error);
    return;
}

var value = result.Value;
```

Failures are logged at appropriate levels and gracefully handled without exceptions.

## Migration & Deployment

### Create New Migrations

```bash
cd src/RalphLoopConsole
dotnet ef migrations add MyMigrationName --startup-project ConsoleApp
```

### Apply Migrations in Production

```bash
dotnet ef database update --startup-project ConsoleApp
```

Migrations are automatically applied on application startup.

## Troubleshooting

### "No pending tasks found"

- Check `Tasks` table: `SELECT COUNT(*) FROM "Tasks" WHERE "Status" = 0;`
- Create test tasks using SQL or API

### "Connection timeout"

- Verify PostgreSQL is running
- Check connection string
- Ensure network connectivity

### "Task stuck in InProgress"

- Check session heartbeat: `SELECT * FROM "ExecutionSessions" WHERE "LastHeartbeat" < NOW() - INTERVAL '5 minutes';`
- Worker may have crashed; restart to trigger reclamation

### "LLM API error"

- Verify `COPILOT_API_KEY` is set
- Check GitHub Copilot API limits
- Review LLM service logs

## Development

### Build

```bash
dotnet build src/RalphLoopConsole/RalphLoopConsole.csproj
```

### Test

```bash
dotnet test tests/RalphLoopConsole.Tests
```

### Code Analysis

```bash
dotnet analyzers run
```

## License

Part of the Daedalus project. See LICENSE file for details.
