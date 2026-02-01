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
        _singleTask = CreateTask(id: 1, executionCount: ExecutionsPerTask);
        _bulkTasks = Enumerable.Range(1, BulkSize)
            .Select(i => CreateTask(i, ExecutionsPerTask))
            .ToList();
    }

    [Benchmark(Description = "DTO Mapping: Single task with 5 executions")]
    public TaskDto MapSingleTask()
    {
        return TaskDtoMapper.ToDto(_singleTask);
    }

    [Benchmark(Description = "DTO Mapping: 10 tasks with 5 executions each")]
    public List<TaskDto> MapBulkSmall()
    {
        return _bulkTasks.Take(10)
            .Select(TaskDtoMapper.ToDto)
            .ToList();
    }

    [Benchmark(Description = "DTO Mapping: 100 tasks with 5 executions each")]
    public List<TaskDto> MapBulkLarge()
    {
        return _bulkTasks
            .Select(TaskDtoMapper.ToDto)
            .ToList();
    }

    [Benchmark(Description = "DTO Mapping: Bulk with ZLinq (zero-allocation iteration)")]
    public List<TaskDto> MapBulkZLinq()
    {
        return _bulkTasks
            .AsValueEnumerable()
            .Select(TaskDtoMapper.ToDto)
            .ToList();
    }

    [Benchmark(Description = "DTO Mapping: Bulk with manual loop")]
    public List<TaskDto> MapBulkManualLoop()
    {
        var dtos = new List<TaskDto>(_bulkTasks.Count);
        foreach (var task in _bulkTasks)
        {
            dtos.Add(TaskDtoMapper.ToDto(task));
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
        var sessionId = Guid.NewGuid();
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
            $"Task {id} prompt content",
            "Task completed successfully",
            100);

        var task = taskResult.Value;
        task.Claim(sessionId);

        for (int i = 0; i < executionCount; i++)
        {
            var isLast = i == executionCount - 1 && id % 2 == 0;
            task.RecordExecution(new TaskExecution
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

        return task;
    }
}
