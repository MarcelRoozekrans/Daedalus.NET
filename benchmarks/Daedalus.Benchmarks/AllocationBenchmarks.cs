namespace Daedalus.Benchmarks;

/// <summary>
/// Benchmarks focused on allocation patterns and memory efficiency.
/// Measures heap allocations for common operations to identify optimization opportunities.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class AllocationBenchmarks
{
    private List<(int Id, string Name)> _tupleList = default!;
    private string[] _stringArray = default!;

    [GlobalSetup]
    public void Setup()
    {
        _tupleList = Enumerable.Range(0, 100)
            .Select(x => (Id: x, Name: $"Item_{x}"))
            .ToList();

        _stringArray = Enumerable.Range(0, 100)
            .Select(x => $"String_{x}")
            .ToArray();
    }

    [Benchmark(Description = "Standard LINQ: Tuple list Select")]
    public List<string> StandardTupleSelect()
    {
        return _tupleList
            .Select(x => x.Name)
            .ToList();
    }

    [Benchmark(Description = "String array iteration - foreach")]
    public int ForeachIteration()
    {
        int count = 0;
        foreach (var str in _stringArray)
            if (str.Length > 5)
                count++;
        return count;
    }

    [Benchmark(Description = "Standard LINQ: String array Where")]
    public List<string> StandardStringWhere()
    {
        return _stringArray
            .Where(x => x.Length > 5)
            .ToList();
    }

    [Benchmark(Description = "StringBuilder: Building 100 items")]
    public string BuildStringBuilder()
    {
        var sb = PerformanceOptimizations.CreateOptimizedBuilder(1000);
        for (int i = 0; i < 100; i++)
        {
            sb.Append($"Item_{i},");
        }
        return sb.ToString();
    }

    [Benchmark(Description = "String concatenation: Building 100 items")]
    public string BuildStringConcat()
    {
        string result = "";
        for (int i = 0; i < 100; i++)
        {
            result += $"Item_{i},";
        }
        return result;
    }
}
