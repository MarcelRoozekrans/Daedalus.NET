namespace Daedalus.Benchmarks;

/// <summary>
/// Benchmarks for hotpath command handlers in Daedalus.
/// Measures performance of critical write operations like task creation and execution.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class CommandHandlerBenchmarks
{
    private string _validPrompt = default!;
    private string _validPromise = default!;
    private string _untrimmedPrompt = default!;
    private string _whitespacePrompt = default!;

    [GlobalSetup]
    public void Setup()
    {
        _validPrompt = "Analyze the given code and identify performance bottlenecks";
        _validPromise = "Return a structured list of optimization opportunities";
        _untrimmedPrompt = "  Untrimmed Prompt  ";
        _whitespacePrompt = "   ";
    }

    [Benchmark(Description = "Validate and trim - Valid prompt")]
    public string ValidateAndTrimValid()
    {
        var result = PerformanceOptimizations.ValidateAndTrimString(_validPrompt, out _);
        return result ?? "";
    }

    [Benchmark(Description = "Validate and trim - Requires trimming")]
    public string ValidateAndTrimRequiresTrim()
    {
        var result = PerformanceOptimizations.ValidateAndTrimString(_untrimmedPrompt, out _);
        return result ?? "";
    }

    [Benchmark(Description = "Validate and trim - Whitespace only")]
    public string ValidateAndTrimWhitespace()
    {
        var result = PerformanceOptimizations.ValidateAndTrimString(_whitespacePrompt, out _);
        return result ?? "";
    }

    [Benchmark(Description = "Standard String.IsNullOrWhiteSpace check")]
    public bool StandardWhitespaceCheck()
    {
        return string.IsNullOrWhiteSpace(_validPrompt);
    }

    [Benchmark(Description = "Multiple validation checks - Command validation")]
    public bool MultipleValidationChecks()
    {
        var promptValid = PerformanceOptimizations.ValidateAndTrimString(_validPrompt, out _) != null;
        var promiseValid = PerformanceOptimizations.ValidateAndTrimString(_validPromise, out _) != null;
        var maxIterationsValid = 5 > 0;
        return promptValid && promiseValid && maxIterationsValid;
    }

    [Benchmark(Description = "ContainsTarget - Substring search")]
    public bool ContainsTargetSearch()
    {
        return PerformanceOptimizations.ContainsTarget(_validPrompt.AsSpan(), "performance".AsSpan());
    }

    [Benchmark(Description = "Standard String.Contains")]
    public bool StandardStringContains()
    {
        return _validPrompt.Contains("performance");
    }

    [Benchmark(Description = "CountOccurrences - Character counting")]
    public int CountOccurrencesTest()
    {
        return PerformanceOptimizations.CountOccurrences(_validPrompt.AsSpan(), ' ');
    }

    [Benchmark(Description = "Standard LINQ Count")]
    public int StandardLinqCount()
    {
        return _validPrompt.Count(c => c == ' ');
    }
}
