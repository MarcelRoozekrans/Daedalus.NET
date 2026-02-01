namespace Daedalus.Benchmarks;

/// <summary>
/// Benchmarks for hotpath query handlers in Daedalus.
/// Measures performance of critical read operations like task retrieval and pagination.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class QueryHandlerBenchmarks
{
    private List<int> _taskIdList = default!;
    private List<(int Id, string Name)> _taskMetadataList = default!;
    private int[] _taskIdArray = default!;
    private const int TaskCount = 1000;

    [GlobalSetup]
    public void Setup()
    {
        _taskIdList = Enumerable.Range(1, TaskCount).ToList();
        _taskIdArray = Enumerable.Range(1, TaskCount).ToArray();
        _taskMetadataList = Enumerable.Range(1, TaskCount)
            .Select(x => (Id: x, Name: $"Task_{x:D4}"))
            .ToList();
    }

    [Benchmark(Description = "Pagination - Skip/Take on 1000 tasks (page 5, size 20)")]
    public List<int> PaginationSkipTake()
    {
        const int page = 5;
        const int pageSize = 20;
        return _taskIdList
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    [Benchmark(Description = "Pagination - Alternative: Direct LINQ Select")]
    public List<int> PaginationDirect()
    {
        const int pageSize = 20;
        const int startIndex = 80; // page 5
        return _taskIdList.GetRange(startIndex, pageSize);
    }

    [Benchmark(Description = "DTO Mapping - Project to tuple list")]
    public List<(int Id, string Name)> DtoMapping()
    {
        return _taskIdList
            .Select(id => (Id: id, Name: $"Task_{id:D4}"))
            .ToList();
    }

    [Benchmark(Description = "Filter + Map - Where + Select")]
    public List<(int Id, string Name)> FilterAndMap()
    {
        return _taskMetadataList
            .Where(t => t.Id % 2 == 0)
            .Select(t => (t.Id, Name: t.Name.ToUpperInvariant()))
            .ToList();
    }

    [Benchmark(Description = "Count before pagination")]
    public int CountOperation()
    {
        return _taskIdList.Count;
    }

    [Benchmark(Description = "Manual iteration - counting matches")]
    public int ManualIteration()
    {
        int count = 0;
        foreach (var taskId in _taskIdArray)
        {
            if (taskId % 3 == 0)
                count++;
        }
        return count;
    }

    [Benchmark(Description = "Standard LINQ Count with filter")]
    public int LinqCountFilter()
    {
        return _taskIdArray.Count(x => x % 3 == 0);
    }

    [Benchmark(Description = "First or default - finding first match")]
    public int FirstOrDefaultMatch()
    {
        return _taskIdList.FirstOrDefault(x => x > 500);
    }

    [Benchmark(Description = "Multiple projections - nested Select")]
    public List<string> MultipleProjections()
    {
        return _taskMetadataList
            .Select(t => t.Name)
            .Select(n => n.ToUpperInvariant())
            .Select(n => $"[{n}]")
            .ToList();
    }

    [Benchmark(Description = "Distinct operation on 1000 tasks")]
    public List<int> DistinctOperation()
    {
        return _taskIdList
            .Concat(_taskIdList.Take(100))  // Add duplicates
            .Distinct()
            .ToList();
    }

    [Benchmark(Description = "Order by operation")]
    public List<int> OrderByOperation()
    {
        return _taskIdList
            .OrderDescending()
            .Take(50)
            .ToList();
    }

    [Benchmark(Description = "Group by operation")]
    public Dictionary<int, List<int>> GroupByOperation()
    {
        return _taskIdList
            .GroupBy(x => x % 10)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    [Benchmark(Description = "Contains check - list membership")]
    public bool ContainsCheck()
    {
        return _taskIdList.Contains(500);
    }

    [Benchmark(Description = "Binary search - array lookup")]
    public int BinarySearch()
    {
        return System.Array.BinarySearch(_taskIdArray, 500);
    }
}
