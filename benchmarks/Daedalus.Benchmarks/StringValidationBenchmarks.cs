namespace Daedalus.Benchmarks;

/// <summary>
/// Benchmarks for string validation and trimming operations.
/// Measures performance of PerformanceOptimizations.ValidateAndTrimString
/// vs standard string validation approaches.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class StringValidationBenchmarks
{
    private string _trimmedString = default!;
    private string _untrimmedString = default!;
    private string _whitespaceString = default!;

    [GlobalSetup]
    public void Setup()
    {
        _trimmedString = "Valid Prompt Text";
        _untrimmedString = "  Untrimmed Prompt Text  ";
        _whitespaceString = "   ";
    }

    [Benchmark(Description = "Standard: IsNullOrWhiteSpace + Trim")]
    public string? StandardValidation()
    {
        if (string.IsNullOrWhiteSpace(_trimmedString))
            return null;
        return _trimmedString.Trim();
    }

    [Benchmark(Description = "PerformanceOptimizations: ValidateAndTrimString (already trimmed)")]
    public string? OptimizedValidationTrimmed()
    {
        var result = PerformanceOptimizations.ValidateAndTrimString(_trimmedString, out _);
        return result;
    }

    [Benchmark(Description = "Standard: IsNullOrWhiteSpace + Trim (untrimmed)")]
    public string? StandardValidationUntrimmed()
    {
        if (string.IsNullOrWhiteSpace(_untrimmedString))
            return null;
        return _untrimmedString.Trim();
    }

    [Benchmark(Description = "PerformanceOptimizations: ValidateAndTrimString (untrimmed)")]
    public string? OptimizedValidationUntrimmed()
    {
        var result = PerformanceOptimizations.ValidateAndTrimString(_untrimmedString, out _);
        return result;
    }

    [Benchmark(Description = "Standard: IsNullOrWhiteSpace (whitespace only)")]
    public bool StandardWhitespaceCheck()
    {
        return string.IsNullOrWhiteSpace(_whitespaceString);
    }

    [Benchmark(Description = "PerformanceOptimizations: ValidateAndTrimString (whitespace only)")]
    public string? OptimizedWhitespaceCheck()
    {
        var result = PerformanceOptimizations.ValidateAndTrimString(_whitespaceString, out _);
        return result;
    }

    [Benchmark(Description = "PerformanceOptimizations: ContainsTarget")]
    public bool ContainsTargetBenchmark()
    {
        return PerformanceOptimizations.ContainsTarget(_trimmedString.AsSpan(), "Prompt".AsSpan());
    }

    [Benchmark(Description = "Standard: String.Contains")]
    public bool StandardContains()
    {
        return _trimmedString.Contains("Prompt");
    }

    [Benchmark(Description = "PerformanceOptimizations: CountOccurrences")]
    public int CountOccurrencesBenchmark()
    {
        return PerformanceOptimizations.CountOccurrences(_trimmedString.AsSpan(), ' ');
    }

    [Benchmark(Description = "Standard: Count with LINQ")]
    public int StandardCountWithLinq()
    {
        return _trimmedString.Count(c => c == ' ');
    }
}
