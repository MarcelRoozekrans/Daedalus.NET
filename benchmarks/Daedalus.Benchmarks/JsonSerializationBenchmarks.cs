namespace Daedalus.Benchmarks;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

/// <summary>
/// Benchmarks for JSON serialization of API responses.
/// Measures performance of serializing DTO collections for HTTP responses.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class JsonSerializationBenchmarks
{
    private JsonSerializerOptions _defaultOptions = default!;
    private JsonSerializerOptions _sourceGenOptions = default!;
    private List<TaskDto> _smallTaskList = default!;
    private List<TaskDto> _bulkTaskList = default!;
    private PagedResultDto<TaskDto> _pagedSmallResult = default!;
    private PagedResultDto<TaskDto> _pagedBulkResult = default!;

    [GlobalSetup]
    public void Setup()
    {
        _defaultOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        _sourceGenOptions = new JsonSerializerOptions(_defaultOptions)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        _smallTaskList = GenerateTaskDtos(10);
        _bulkTaskList = GenerateTaskDtos(100);

        _pagedSmallResult = new PagedResultDto<TaskDto>(_smallTaskList, 50, 1, 10);
        _pagedBulkResult = new PagedResultDto<TaskDto>(_bulkTaskList, 500, 1, 100);
    }

    [Benchmark(Description = "JSON: Serialize 10 tasks (default options)")]
    public string SerializeSmall()
    {
        return JsonSerializer.Serialize(_smallTaskList, _defaultOptions);
    }

    [Benchmark(Description = "JSON: Serialize 100 tasks (default options)")]
    public string SerializeBulk()
    {
        return JsonSerializer.Serialize(_bulkTaskList, _defaultOptions);
    }

    [Benchmark(Description = "JSON: Serialize paged result (10 tasks)")]
    public string SerializePagedSmall()
    {
        return JsonSerializer.Serialize(_pagedSmallResult, _defaultOptions);
    }

    [Benchmark(Description = "JSON: Serialize paged result (100 tasks)")]
    public string SerializePagedBulk()
    {
        return JsonSerializer.Serialize(_pagedBulkResult, _defaultOptions);
    }

    [Benchmark(Description = "JSON: Single task serialization")]
    public string SerializeSingleTask()
    {
        return JsonSerializer.Serialize(_smallTaskList[0], _defaultOptions);
    }

    [Benchmark(Description = "JSON: Serialize to UTF8 bytes (10 tasks)")]
    public byte[] SerializeToUtf8Small()
    {
        return JsonSerializer.SerializeToUtf8Bytes(_smallTaskList, _defaultOptions);
    }

    [Benchmark(Description = "JSON: Serialize to UTF8 bytes (100 tasks)")]
    public byte[] SerializeToUtf8Bulk()
    {
        return JsonSerializer.SerializeToUtf8Bytes(_bulkTaskList, _defaultOptions);
    }

    private static List<TaskDto> GenerateTaskDtos(int count)
    {
        return Enumerable.Range(1, count)
            .Select(i => new TaskDto(
                Guid.NewGuid(),
                $"TASK-{i:D3}",
                Guid.NewGuid(),
                $"Task {i} title",
                $"Task {i} description for testing",
                i % 3,
                $"Phase-{i % 3 + 1}",
                i % 3 + 1,
                new List<string> { $"TASK-{Math.Max(1, i - 1):D3}" },
                new List<string> { $"src/File{i}.cs" },
                i % 3,
                $"Task {i} prompt content for testing serialization",
                "Task completed successfully",
                10,
                i % 3,
                Guid.NewGuid(),
                i % 2 == 0 ? "Success" : null,
                i % 10,
                DateTime.UtcNow.AddDays(-i),
                i % 2 == 0 ? DateTime.UtcNow : null,
                i % 2 == 0 ? "Some learnings" : null,
                i % 2 == 0 ? DateTime.UtcNow : null,
                Enumerable.Range(1, 5)
                    .Select(j => new TaskExecutionDto(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        j,
                        $"Execution {j} prompt",
                        $"LLM Response {j}: {string.Concat(Enumerable.Repeat("x", 200))}",
                        j % 2 == 0,
                        DateTime.UtcNow.AddMinutes(-j),
                        TimeSpan.FromMilliseconds(100 + j * 10),
                        null
                    ))
                    .ToList()
            ))
            .ToList();
    }
}
