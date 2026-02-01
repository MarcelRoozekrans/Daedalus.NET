namespace Daedalus.Benchmarks;

using System.Text;

/// <summary>
/// Benchmarks for LLM response text extraction patterns used in ClaudeLlmService
/// and enhanced prompt building. Exercises the StringBuilder accumulation,
/// string.Insert on multi-KB prompts, and multi-block response assembly patterns.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class ResponseExtractionBenchmarks
{
    private List<string> _textBlocks3 = default!;
    private List<string> _textBlocks10 = default!;
    private List<string> _textBlocks25 = default!;
    private string _smallPrompt = default!;      // ~1KB
    private string _largePrompt = default!;       // ~10KB
    private string _hugePrompt = default!;        // ~50KB
    private string _injectionContent = default!;  // ~500 chars
    private readonly string _insertionMarker = "=== YOUR TASK ===";

    [GlobalSetup]
    public void Setup()
    {
        // Simulate multi-block Claude responses (ExtractTextContent pattern)
        _textBlocks3 = GenerateBlocks(3, 200);
        _textBlocks10 = GenerateBlocks(10, 200);
        _textBlocks25 = GenerateBlocks(25, 500);

        // Prompts of varying sizes for insertion benchmarks
        _smallPrompt = GeneratePromptWithMarker(1_000);
        _largePrompt = GeneratePromptWithMarker(10_000);
        _hugePrompt = GeneratePromptWithMarker(50_000);

        _injectionContent = $"\n\n=== AGENT GUIDANCE ===\n{new string('g', 400)}\n\n";
    }

    // === ExtractTextContent pattern (from ClaudeLlmService) ===

    [Benchmark(Description = "ExtractTextContent: 3 blocks via StringBuilder")]
    public string ExtractText3Blocks()
    {
        return ExtractTextContentStringBuilder(_textBlocks3);
    }

    [Benchmark(Description = "ExtractTextContent: 10 blocks via StringBuilder")]
    public string ExtractText10Blocks()
    {
        return ExtractTextContentStringBuilder(_textBlocks10);
    }

    [Benchmark(Description = "ExtractTextContent: 25 blocks via StringBuilder")]
    public string ExtractText25Blocks()
    {
        return ExtractTextContentStringBuilder(_textBlocks25);
    }

    [Benchmark(Description = "ExtractTextContent: 3 blocks via string.Join")]
    public string ExtractText3BlocksJoin()
    {
        return string.Join(string.Empty, _textBlocks3).Trim();
    }

    [Benchmark(Description = "ExtractTextContent: 10 blocks via string.Join")]
    public string ExtractText10BlocksJoin()
    {
        return string.Join(string.Empty, _textBlocks10).Trim();
    }

    [Benchmark(Description = "ExtractTextContent: 25 blocks via string.Join")]
    public string ExtractText25BlocksJoin()
    {
        return string.Join(string.Empty, _textBlocks25).Trim();
    }

    [Benchmark(Description = "ExtractTextContent: 10 blocks via string.Concat")]
    public string ExtractText10BlocksConcat()
    {
        return string.Concat(_textBlocks10).Trim();
    }

    // === String.Insert pattern (from McpEnhancedPromptBuilder) ===

    [Benchmark(Description = "String.Insert into 1KB prompt")]
    public string InsertIntoSmallPrompt()
    {
        var index = _smallPrompt.IndexOf(_insertionMarker, StringComparison.Ordinal);
        return index >= 0 ? _smallPrompt.Insert(index, _injectionContent) : _smallPrompt;
    }

    [Benchmark(Description = "String.Insert into 10KB prompt")]
    public string InsertIntoLargePrompt()
    {
        var index = _largePrompt.IndexOf(_insertionMarker, StringComparison.Ordinal);
        return index >= 0 ? _largePrompt.Insert(index, _injectionContent) : _largePrompt;
    }

    [Benchmark(Description = "String.Insert into 50KB prompt")]
    public string InsertIntoHugePrompt()
    {
        var index = _hugePrompt.IndexOf(_insertionMarker, StringComparison.Ordinal);
        return index >= 0 ? _hugePrompt.Insert(index, _injectionContent) : _hugePrompt;
    }

    [Benchmark(Description = "StringBuilder.Insert into 10KB prompt (alternative)")]
    public string SbInsertIntoLargePrompt()
    {
        var sb = new StringBuilder(_largePrompt);
        var index = _largePrompt.IndexOf(_insertionMarker, StringComparison.Ordinal);
        if (index >= 0)
        {
            sb.Insert(index, _injectionContent);
        }
        return sb.ToString();
    }

    [Benchmark(Description = "StringBuilder.Insert into 50KB prompt (alternative)")]
    public string SbInsertIntoHugePrompt()
    {
        var sb = new StringBuilder(_hugePrompt);
        var index = _hugePrompt.IndexOf(_insertionMarker, StringComparison.Ordinal);
        if (index >= 0)
        {
            sb.Insert(index, _injectionContent);
        }
        return sb.ToString();
    }

    // === IndexOf scan cost on large strings ===

    [Benchmark(Description = "IndexOf marker in 1KB prompt")]
    public int IndexOfSmall()
    {
        return _smallPrompt.IndexOf(_insertionMarker, StringComparison.Ordinal);
    }

    [Benchmark(Description = "IndexOf marker in 10KB prompt")]
    public int IndexOfLarge()
    {
        return _largePrompt.IndexOf(_insertionMarker, StringComparison.Ordinal);
    }

    [Benchmark(Description = "IndexOf marker in 50KB prompt")]
    public int IndexOfHuge()
    {
        return _hugePrompt.IndexOf(_insertionMarker, StringComparison.Ordinal);
    }

    // === Helpers ===

    private static string ExtractTextContentStringBuilder(List<string> blocks)
    {
        // Mirrors ClaudeLlmService.ExtractTextContent pattern
        if (blocks.Count == 1)
        {
            return blocks[0].Trim();
        }

        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            sb.Append(block);
        }
        return sb.ToString().Trim();
    }

    private static List<string> GenerateBlocks(int count, int avgSize)
    {
        var blocks = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            blocks.Add($"Block {i}: {new string('x', avgSize)} ");
        }
        return blocks;
    }

    private string GeneratePromptWithMarker(int totalSize)
    {
        var sb = new StringBuilder(totalSize);
        var beforeMarker = totalSize * 3 / 4; // Marker at ~75% of prompt

        sb.Append(new string('p', beforeMarker));
        sb.AppendLine();
        sb.AppendLine(_insertionMarker);
        var remaining = totalSize - beforeMarker - _insertionMarker.Length - 4;
        if (remaining > 0)
        {
            sb.Append(new string('s', remaining));
        }

        return sb.ToString();
    }
}
