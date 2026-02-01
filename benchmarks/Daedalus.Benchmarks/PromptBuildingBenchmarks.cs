namespace Daedalus.Benchmarks;

using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using ZLinq;

/// <summary>
/// Benchmarks for the Ralph loop prompt building pipeline — the single hottest
/// allocation path in the system. BuildPromptAsync is called every iteration and
/// BuildEnhancedPrompt assembles history context with multiple LINQ passes.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class PromptBuildingBenchmarks
{
    private RalphPromptTemplateBuilder _builder = default!;
    private RalphPromptTemplateOptions _minimalOptions = default!;
    private RalphPromptTemplateOptions _fullOptions = default!;
    private RalphPromptTemplateOptions _optionsWithWorkspace = default!;
    private List<PromptSection> _sections20 = default!;
    private List<PromptSection> _sections50 = default!;
    private List<PromptHistoryEntry> _historySmall = default!;
    private List<PromptHistoryEntry> _historyLarge = default!;

    [GlobalSetup]
    public void Setup()
    {
        _builder = new RalphPromptTemplateBuilder(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RalphPromptTemplateBuilder>.Instance);

        _minimalOptions = new RalphPromptTemplateOptions
        {
            TaskPrompt = "Implement Redis caching for API response endpoints",
            CompletionPromise = "Task completed successfully",
            IncludeGitWorkflow = false,
            IncludeTestInstructions = false,
            IncludeQualityGuards = false,
            IncludeSelfImprovement = false,
            IncludeCodingStandards = false,
            IncludeLoggingInstructions = false
        };

        _fullOptions = new RalphPromptTemplateOptions
        {
            TaskPrompt = "Implement Redis caching for API response endpoints with TTL management and cache invalidation",
            CompletionPromise = "Task completed successfully",
            IncludeGitWorkflow = true,
            IncludeTestInstructions = true,
            IncludeQualityGuards = true,
            IncludeSelfImprovement = true,
            IncludeCodingStandards = false,
            IncludeLoggingInstructions = true,
            MaxParallelSubagents = 10
        };

        _optionsWithWorkspace = new RalphPromptTemplateOptions
        {
            TaskPrompt = "Implement Redis caching for API response endpoints with TTL management",
            CompletionPromise = "Task completed successfully",
            IncludeGitWorkflow = true,
            IncludeTestInstructions = true,
            IncludeQualityGuards = true,
            IncludeSelfImprovement = true,
            IncludeCodingStandards = true,
            IncludeLoggingInstructions = true,
            MaxParallelSubagents = 10,
            WorkspaceContext = new WorkspaceContext
            {
                WorkspacePath = "/workspace",
                Specifications = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["specs/api.md"] = new string('x', 2000),
                    ["specs/architecture.md"] = new string('y', 3000),
                    ["specs/testing.md"] = new string('z', 1500)
                },
                PlanContent = new string('p', 1000),
                PlanFilePath = "fix_plan.md",
                AgentInstructions = new string('a', 500),
                AgentInstructionsPath = "AGENT.md",
                CopilotInstructions = new string('c', 4000),
                CopilotInstructionsPath = ".github/copilot-instructions.md",
                EditorConfig = new string('e', 800)
            }
        };

        // Pre-build prompt sections for assembly benchmarks
        _sections20 = GenerateSections(20);
        _sections50 = GenerateSections(50);

        // History entries for enhanced prompt building
        _historySmall = GenerateHistory(3);
        _historyLarge = GenerateHistory(20);
    }

    // === Section Generation Benchmarks ===

    [Benchmark(Description = "GetDefaultSections - minimal (3 sections)")]
    public IReadOnlyList<PromptSection> GetSectionsMinimal()
    {
        return _builder.GetDefaultSections(_minimalOptions).Value;
    }

    [Benchmark(Description = "GetDefaultSections - full (15+ sections with subagents)")]
    public IReadOnlyList<PromptSection> GetSectionsFull()
    {
        return _builder.GetDefaultSections(_fullOptions).Value;
    }

    [Benchmark(Description = "GetDefaultSections - with workspace context + coding standards")]
    public IReadOnlyList<PromptSection> GetSectionsWithWorkspace()
    {
        return _builder.GetDefaultSections(_optionsWithWorkspace).Value;
    }

    // === Full Prompt Build Benchmarks ===

    [Benchmark(Description = "BuildPromptAsync - minimal options")]
    public string BuildPromptMinimal()
    {
        return _builder.BuildPromptAsync(_minimalOptions).Result.Value;
    }

    [Benchmark(Description = "BuildPromptAsync - full options (15+ sections)")]
    public string BuildPromptFull()
    {
        return _builder.BuildPromptAsync(_fullOptions).Result.Value;
    }

    [Benchmark(Description = "BuildPromptAsync - full with workspace context")]
    public string BuildPromptWithWorkspace()
    {
        return _builder.BuildPromptAsync(_optionsWithWorkspace).Result.Value;
    }

    // === Section Assembly Benchmarks ===

    [Benchmark(Description = "PromptSection.Create - single section")]
    public PromptSection PromptSectionCreate()
    {
        return PromptSection.Create(10, "1", "Implement the Redis caching layer", PromptSectionCategory.Task).Value;
    }

    [Benchmark(Description = "PromptSection.Create - 20 sections")]
    public List<PromptSection> PromptSectionCreateBulk()
    {
        var sections = new List<PromptSection>(20);
        for (int i = 0; i < 20; i++)
        {
            sections.Add(PromptSection.Create(
                i * 10, $"{i}", $"Section {i} content for prompt building", PromptSectionCategory.Task).Value);
        }
        return sections;
    }

    // === ZLinq Filter + Sort Pipeline (from GetDefaultSections) ===

    [Benchmark(Description = "ZLinq filter + sort 20 sections (as in GetDefaultSections)")]
    public List<PromptSection> ZLinqFilterSort20()
    {
        return _sections20
            .AsValueEnumerable()
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Priority)
            .ToList();
    }

    [Benchmark(Description = "Standard LINQ filter + sort 20 sections")]
    public List<PromptSection> StandardLinqFilterSort20()
    {
        return _sections20
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Priority)
            .ToList();
    }

    [Benchmark(Description = "ZLinq filter + sort 50 sections")]
    public List<PromptSection> ZLinqFilterSort50()
    {
        return _sections50
            .AsValueEnumerable()
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Priority)
            .ToList();
    }

    // === History Iteration Benchmarks (from BuildEnhancedPrompt) ===

    [Benchmark(Description = "History TakeLast(5).ToList() - 3 entries")]
    public List<PromptHistoryEntry> TakeLastSmall()
    {
        return _historySmall.TakeLast(5).ToList();
    }

    [Benchmark(Description = "History TakeLast(5).ToList() - 20 entries")]
    public List<PromptHistoryEntry> TakeLastLarge()
    {
        return _historyLarge.TakeLast(5).ToList();
    }

    [Benchmark(Description = "History Count(predicate) - 3 passes over 20 entries")]
    public (int Failures, int Incomplete, int Progress) HistoryCountPasses()
    {
        var failures = _historyLarge.Count(h => !string.IsNullOrEmpty(h.ExecutionError));
        var incomplete = _historyLarge.Count(h =>
            string.IsNullOrEmpty(h.ExecutionError) && !h.CompletionPromiseFound);
        var progress = _historyLarge.Count(h =>
            !h.CompletionPromiseFound && string.IsNullOrEmpty(h.ExecutionError));
        return (failures, incomplete, progress);
    }

    [Benchmark(Description = "History Count - single pass with manual loop")]
    public (int Failures, int Incomplete, int Progress) HistoryCountSinglePass()
    {
        int failures = 0, incomplete = 0, progress = 0;
        foreach (var h in _historyLarge)
        {
            var hasError = !string.IsNullOrEmpty(h.ExecutionError);
            if (hasError)
            {
                failures++;
            }
            else if (!h.CompletionPromiseFound)
            {
                incomplete++;
                progress++;
            }
        }
        return (failures, incomplete, progress);
    }

    [Benchmark(Description = "Response.Substring(0, 200) snippet extraction")]
    public string SubstringSnippet()
    {
        var response = _historyLarge[0].Response;
        return response.Length > 200
            ? response.Substring(0, 200) + "..."
            : response;
    }

    [Benchmark(Description = "Response snippet via Span (zero-alloc comparison)")]
    public ReadOnlySpan<char> SpanSnippet()
    {
        var response = _historyLarge[0].Response;
        return response.Length > 200
            ? response.AsSpan(0, 200)
            : response.AsSpan();
    }

    // === WorkspaceContext.TotalCharactersLoaded ===

    [Benchmark(Description = "WorkspaceContext.TotalCharactersLoaded (LINQ Sum over dict)")]
    public int WorkspaceContextTotalChars()
    {
        return _optionsWithWorkspace.WorkspaceContext!.TotalCharactersLoaded;
    }

    // === Helpers ===

    private static List<PromptSection> GenerateSections(int count)
    {
        var sections = new List<PromptSection>(count);
        for (int i = 0; i < count; i++)
        {
            var category = (PromptSectionCategory)(i % 11);
            sections.Add(PromptSection.Create(
                i * 10,
                $"{i}",
                $"Section {i} content: {new string('x', 50 + i * 5)}",
                category,
                i % 10 != 0 // 10% disabled
            ).Value);
        }
        return sections;
    }

    private static List<PromptHistoryEntry> GenerateHistory(int count)
    {
        var history = new List<PromptHistoryEntry>(count);
        for (int i = 0; i < count; i++)
        {
            history.Add(new PromptHistoryEntry
            {
                Iteration = i + 1,
                Prompt = $"Iteration {i + 1} prompt text",
                Response = $"LLM response for iteration {i + 1}: {new string('r', 500)}",
                CompletionPromiseFound = i == count - 1,
                Duration = TimeSpan.FromMilliseconds(100 + i * 50),
                ExecutionError = i % 5 == 0 ? "Timeout error" : null,
                ExecutedAt = DateTime.UtcNow.AddMinutes(-count + i)
            });
        }
        return history;
    }
}
