namespace Daedalus.Benchmarks;

using System.Globalization;

/// <summary>
/// Benchmarks for LLM response processing hotpaths.
/// Measures performance of completion promise detection in large language model responses.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class LlmResponseBenchmarks
{
    private string _smallResponse = default!;
    private string _largeResponse = default!;
    private string _completionPromise = default!;
    private string _completionPromiseLower = default!;

    [GlobalSetup]
    public void Setup()
    {
        _completionPromise = "Task completed successfully";
        _completionPromiseLower = _completionPromise.ToLowerInvariant();

        // Simulate realistic LLM responses
        _smallResponse = GenerateLlmResponse(200);  // ~200 chars
        _largeResponse = GenerateLlmResponse(2000); // ~2KB typical response
    }

    [Benchmark(Description = "LLM: String.Contains with OrdinalIgnoreCase (small response)")]
    public bool ContainsSmallResponseStandard()
    {
        return _smallResponse.Contains(_completionPromise, StringComparison.OrdinalIgnoreCase);
    }

    [Benchmark(Description = "LLM: String.Contains with OrdinalIgnoreCase (large response)")]
    public bool ContainsLargeResponseStandard()
    {
        return _largeResponse.Contains(_completionPromise, StringComparison.OrdinalIgnoreCase);
    }

    [Benchmark(Description = "LLM: IndexOf case-insensitive (large response)")]
    public int IndexOfCaseInsensitive()
    {
        return _largeResponse.IndexOf(_completionPromise, StringComparison.OrdinalIgnoreCase);
    }

    [Benchmark(Description = "LLM: Pre-lowercased + Contains (large response)")]
    public bool ContainsPreLowercased()
    {
        return _largeResponse.ToLowerInvariant().Contains(_completionPromiseLower);
    }

    [Benchmark(Description = "LLM: IndexOf on pre-lowercased (large response)")]
    public int IndexOfPreLowercased()
    {
        return _largeResponse.ToLowerInvariant().IndexOf(_completionPromiseLower);
    }

    [Benchmark(Description = "LLM: First character match (case-insensitive)")]
    public int FirstCharacterMatch()
    {
        var firstChar = char.ToLowerInvariant(_completionPromise[0]);
        return _largeResponse.IndexOf(firstChar, StringComparison.OrdinalIgnoreCase);
    }

    [Benchmark(Description = "LLM: Span-based IndexOf (large response)")]
    public int SpanIndexOf()
    {
        var responseLower = _largeResponse.ToLowerInvariant();
        return responseLower.AsSpan().IndexOf(_completionPromiseLower.AsSpan());
    }

    [Benchmark(Description = "LLM: Manual case-insensitive search")]
    public bool ManualCaseInsensitiveSearch()
    {
        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            _largeResponse, _completionPromise, CompareOptions.IgnoreCase) >= 0;
    }

    private static string GenerateLlmResponse(int length)
    {
        var sb = new System.Text.StringBuilder(length);
        var text = "The analysis reveals several optimization opportunities. " +
                   "Performance metrics show a 25% improvement. " +
                   "The system responds within SLA limits. " +
                   "Task completed successfully. ";

        while (sb.Length < length)
        {
            sb.Append(text);
        }

        return sb.ToString(0, length);
    }
}
