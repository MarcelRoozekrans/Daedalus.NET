using Daedalus.Application.Abstractions;
namespace Daedalus.Benchmarks;

using ZLinq;

/// <summary>
/// Benchmarks for DTO mapping patterns at scale.
/// Measures performance of mapping domain entities to DTOs for API responses.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class DtoMappingBenchmarks
{
    private Task _singleTask = default!;
    private List<Task> _bulkTasks = default!;
    private const int BulkSize = 100;
    private const int ExecutionsPerTask = 5;

    [GlobalSetup]
    public void Setup()
    {
        // Nothing but the retired loop wrote a task's executions, and Task has no member that adds one. The store
        // loads a task with its executions through Task.Rehydrate, and so does this setup.
        _singleTask = CreateTask(id: 1, ExecutionsPerTask);
        _bulkTasks = Enumerable.Range(1, BulkSize)
            .Select(i => CreateTask(i, ExecutionsPerTask))
            .ToList();
    }

    [Benchmark(Description = "DTO Mapping: Single task with 5 executions")]
    public TaskDto MapSingleTask()
    {
        return TaskDtoMapper.ToDto(_singleTask, WorkflowRunStatus.Unknown);
    }

    [Benchmark(Description = "DTO Mapping: 10 tasks with 5 executions each")]
    public List<TaskDto> MapBulkSmall()
    {
        return _bulkTasks.Take(10)
            .Select(t => TaskDtoMapper.ToDto(t, WorkflowRunStatus.Unknown))
            .ToList();
    }

    [Benchmark(Description = "DTO Mapping: 100 tasks with 5 executions each")]
    public List<TaskDto> MapBulkLarge()
    {
        return _bulkTasks
            .Select(t => TaskDtoMapper.ToDto(t, WorkflowRunStatus.Unknown))
            .ToList();
    }

    [Benchmark(Description = "DTO Mapping: Bulk with ZLinq (zero-allocation iteration)")]
    public List<TaskDto> MapBulkZLinq()
    {
        return _bulkTasks
            .AsValueEnumerable()
            .Select(t => TaskDtoMapper.ToDto(t, WorkflowRunStatus.Unknown))
            .ToList();
    }

    [Benchmark(Description = "DTO Mapping: Bulk with manual loop")]
    public List<TaskDto> MapBulkManualLoop()
    {
        var dtos = new List<TaskDto>(_bulkTasks.Count);
        foreach (var task in _bulkTasks)
        {
            dtos.Add(TaskDtoMapper.ToDto(task, WorkflowRunStatus.Unknown));
        }
        return dtos;
    }

    [Benchmark(Description = "DTO Mapping: Only executions (nested list allocation)")]
    public List<List<TaskExecutionDto>> MapExecutionsOnly()
    {
        return _bulkTasks
            .Select(t => t.Executions
                .Select(TaskDtoMapper.ToExecutionDto)
                .ToList())
            .ToList();
    }

    private static Task CreateTask(int id, int executionCount)
    {
        var taskResult = Task.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"TASK-{id:D3}",
            $"Task {id} title",
            $"Task {id} description content",
            (Priority)(id % 3),
            $"Phase-{id % 3 + 1}",
            id % 3 + 1,
            (Complexity)(id % 3),
            $"Task {id} prompt content");

        var task = taskResult.Value;
        return Task.Rehydrate(
            task.Id, task.TaskId, task.ProjectId, task.Title, task.Description, task.Priority, task.Status, task.Phase,
            task.ParallelGroup, task.EstimatedComplexity, task.Prompt, task.CompletionPromise, task.MaxIterations,
            task.CurrentSessionId, task.Result, task.IterationCount, task.CreatedAt, task.CompletedAt, task.Learnings,
            task.LearningsUpdatedAt, task.Dependencies, task.FilesToModify, task.WorkflowRunId, task.RowVersion,
            CreateExecutions(task, id, executionCount));
    }

    private static List<TaskExecution> CreateExecutions(Task task, int id, int executionCount)
    {
        var sessionId = Guid.NewGuid();
        var executions = new List<TaskExecution>(executionCount);

        for (int i = 0; i < executionCount; i++)
        {
            var isLast = i == executionCount - 1 && id % 2 == 0;
            executions.Add(new TaskExecution
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                SessionId = sessionId,
                IterationNumber = i + 1,
                Prompt = task.Prompt,
                LlmResponse = isLast
                    ? "Task completed successfully"
                    : $"LLM Response {i}: {string.Concat(Enumerable.Repeat("x", 100))}",
                CompletionPromiseFound = isLast,
                ExecutedAt = DateTime.UtcNow.AddMinutes(-i),
                ExecutionDuration = TimeSpan.FromMilliseconds(100 + i * 10),
                Error = null
            });
        }

        return executions;
    }
}
