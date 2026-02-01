namespace Daedalus.Benchmarks;

using CSharpFunctionalExtensions;

/// <summary>
/// Benchmarks for domain entity creation, state transitions, and collection operations.
/// These are hot paths in the Ralph loop: Task.Create is called per task,
/// Task.RecordExecution is called every iteration, and dependency manipulation
/// exercises linear-search patterns on List&lt;string&gt;.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class DomainEntityBenchmarks
{
    private Task _inProgressTask = default!;
    private Task _taskWithDependencies = default!;
    private Task _taskWithFiles = default!;
    private Guid _sessionId;

    // Pre-allocated valid strings (already trimmed) to isolate entity logic from string allocation
    private const string ValidTaskId = "TASK-001";
    private const string ValidTitle = "Implement caching layer";
    private const string ValidDescription = "Add Redis caching for hot path queries";
    private const string ValidPhase = "Backend";
    private const string ValidPrompt = "Implement a Redis-backed distributed cache for API responses";
    private const string ValidPromise = "Task completed successfully";

    // Untrimmed strings to measure Trim() allocation inside Create
    private const string UntrimmedTitle = "  Implement caching layer  ";
    private const string UntrimmedDescription = "  Add Redis caching for hot path queries  ";
    private const string UntrimmedPhase = "  Backend  ";
    private const string UntrimmedPrompt = "  Implement caching  ";
    private const string UntrimmedPromise = "  Done  ";

    [GlobalSetup]
    public void Setup()
    {
        _sessionId = Guid.NewGuid();

        // Create a task that's in-progress for RecordExecution benchmarks
        var taskResult = Task.Create(
            Guid.NewGuid(), Guid.NewGuid(), ValidTaskId, ValidTitle, ValidDescription,
            Priority.High, ValidPhase, 1, Complexity.High,
            ValidPrompt, ValidPromise, 100);
        _inProgressTask = taskResult.Value;
        _inProgressTask.Claim(_sessionId);

        // Create a task with many dependencies for linear-search benchmarks
        var depTaskResult = Task.Create(
            Guid.NewGuid(), Guid.NewGuid(), "TASK-DEP", "Dep task", "Has many deps",
            Priority.Medium, "Phase-1", 1, Complexity.Medium,
            "Do work", "Done", 10);
        _taskWithDependencies = depTaskResult.Value;
        for (int i = 0; i < 50; i++)
        {
            _taskWithDependencies.AddDependency($"TASK-{i:D3}");
        }

        // Create a task with many files for file list search benchmarks
        var fileTaskResult = Task.Create(
            Guid.NewGuid(), Guid.NewGuid(), "TASK-FILE", "File task", "Has many files",
            Priority.Medium, "Phase-1", 1, Complexity.Medium,
            "Do work", "Done", 10);
        _taskWithFiles = fileTaskResult.Value;
        for (int i = 0; i < 50; i++)
        {
            _taskWithFiles.AddFileToModify($"src/Module{i}/Service.cs");
        }

        // Pre-allocate for setup reference
        _ = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = _inProgressTask.Id,
            SessionId = _sessionId,
            IterationNumber = _inProgressTask.IterationCount + 1,
            Prompt = ValidPrompt,
            LlmResponse = "Response text without completion promise",
            CompletionPromiseFound = false,
            ExecutedAt = DateTime.UtcNow,
            ExecutionDuration = TimeSpan.FromMilliseconds(150)
        };
    }

    [Benchmark(Description = "Task.Create - pre-trimmed strings")]
    public Result<Task> TaskCreateTrimmed()
    {
        return Task.Create(
            Guid.NewGuid(), Guid.NewGuid(), ValidTaskId, ValidTitle, ValidDescription,
            Priority.High, ValidPhase, 1, Complexity.High,
            ValidPrompt, ValidPromise, 50);
    }

    [Benchmark(Description = "Task.Create - untrimmed strings (forces Trim allocation)")]
    public Result<Task> TaskCreateUntrimmed()
    {
        return Task.Create(
            Guid.NewGuid(), Guid.NewGuid(), "  TASK-002  ", UntrimmedTitle, UntrimmedDescription,
            Priority.High, UntrimmedPhase, 1, Complexity.High,
            UntrimmedPrompt, UntrimmedPromise, 50);
    }

    [Benchmark(Description = "Task.Create - validation failure (empty title)")]
    public Result<Task> TaskCreateValidationFailure()
    {
        return Task.Create(
            Guid.NewGuid(), Guid.NewGuid(), ValidTaskId, "", ValidDescription,
            Priority.High, ValidPhase, 1, Complexity.High,
            ValidPrompt, ValidPromise, 50);
    }

    [Benchmark(Description = "Task.RecordExecution - incomplete (hotpath per iteration)")]
    public Result TaskRecordExecution()
    {
        // Reset task state for repeatable benchmarking
        var task = Task.Create(
            Guid.NewGuid(), Guid.NewGuid(), ValidTaskId, ValidTitle, ValidDescription,
            Priority.High, ValidPhase, 1, Complexity.High,
            ValidPrompt, ValidPromise, 100).Value;
        task.Claim(_sessionId);

        return task.RecordExecution(new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SessionId = _sessionId,
            IterationNumber = 1,
            Prompt = ValidPrompt,
            LlmResponse = "No completion found",
            CompletionPromiseFound = false,
            ExecutedAt = DateTime.UtcNow,
            ExecutionDuration = TimeSpan.FromMilliseconds(150)
        });
    }

    [Benchmark(Description = "Task.RecordExecution - with completion (state transition)")]
    public Result TaskRecordExecutionCompleted()
    {
        var task = Task.Create(
            Guid.NewGuid(), Guid.NewGuid(), ValidTaskId, ValidTitle, ValidDescription,
            Priority.High, ValidPhase, 1, Complexity.High,
            ValidPrompt, ValidPromise, 100).Value;
        task.Claim(_sessionId);

        return task.RecordExecution(new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SessionId = _sessionId,
            IterationNumber = 1,
            Prompt = ValidPrompt,
            LlmResponse = "Task completed successfully",
            CompletionPromiseFound = true,
            ExecutedAt = DateTime.UtcNow,
            ExecutionDuration = TimeSpan.FromMilliseconds(150)
        });
    }

    [Benchmark(Description = "Task.AddDependency - to list of 50 (linear Contains check)")]
    public Result AddDependencyToLargeList()
    {
        // Adding a new dependency that doesn't exist yet (worst-case linear scan)
        var task = Task.Create(
            Guid.NewGuid(), Guid.NewGuid(), "TASK-ADD", "Add task", "Add deps",
            Priority.Medium, "Phase-1", 1, Complexity.Medium,
            "Do work", "Done", 10).Value;
        for (int i = 0; i < 50; i++)
        {
            task.AddDependency($"TASK-{i:D3}");
        }

        return task.AddDependency("TASK-999");
    }

    [Benchmark(Description = "Task.AddDependency - duplicate check (found at end of 50)")]
    public Result AddDependencyDuplicate()
    {
        // Trying to add a dependency that exists at the end (worst-case scan)
        return _taskWithDependencies.AddDependency("TASK-049");
    }

    [Benchmark(Description = "Task.AddFileToModify - to list of 50")]
    public Result AddFileToLargeList()
    {
        return _taskWithFiles.AddFileToModify("src/NewModule/Service.cs");
    }

    [Benchmark(Description = "Project.Create - factory method")]
    public Result<Project> ProjectCreate()
    {
        return Project.Create(
            Guid.NewGuid(), "Daedalus Project", "High-perf .NET 10 task execution", "2.0");
    }

    [Benchmark(Description = "Project.AddTask - duplicate check on 20 tasks")]
    public Result ProjectAddTask()
    {
        var project = Project.Create(Guid.NewGuid(), "Project", "Desc", "1.0").Value;
        for (int i = 0; i < 20; i++)
        {
            var t = Task.Create(
                Guid.NewGuid(), project.Id, $"TASK-{i:D3}", $"Title {i}", $"Desc {i}",
                Priority.Medium, "Phase-1", 1, Complexity.Medium,
                "Prompt", "Done", 10).Value;
            project.AddTask(t);
        }

        // Add one more (exercises Exists check across 20 items)
        var newTask = Task.Create(
            Guid.NewGuid(), project.Id, "TASK-NEW", "New", "New",
            Priority.High, "Phase-2", 1, Complexity.Low,
            "Prompt", "Done", 10).Value;

        return project.AddTask(newTask);
    }

    [Benchmark(Description = "ExecutionSession.Create + IsStale check")]
    public bool ExecutionSessionCreateAndStaleCheck()
    {
        var session = ExecutionSession.Create(Guid.NewGuid(), "worker-001").Value;
        session.Heartbeat();
        return session.IsStale(TimeSpan.FromMinutes(5));
    }

    [Benchmark(Description = "Task.UpdateMetadata - 3x Trim allocations")]
    public Result TaskUpdateMetadata()
    {
        return _inProgressTask.UpdateMetadata(
            UntrimmedTitle, UntrimmedDescription, Priority.High, UntrimmedPhase, Complexity.High);
    }
}
