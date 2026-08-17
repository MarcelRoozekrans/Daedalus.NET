# MCP Knowledge Base Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Give the Ralph Loop LLM on-demand semantic search over learnings and failure patterns via local MCP tools, replacing bulk text injection with targeted queries.

**Architecture:** In-process MCP server using `ModelContextProtocol` SDK exposes two tools (`search_learnings`, `search_failure_patterns`) backed by pgvector semantic search. Ollama `nomic-embed-text` generates embeddings. Three-level fallback ensures the Ralph Loop never breaks.

**Tech Stack:** .NET 10, EF Core 10, PostgreSQL with pgvector, Ollama, ModelContextProtocol SDK, Microsoft.Extensions.AI, Aspire 13

---

### Task 1: Add pgvector and Ollama NuGet Packages

**Files:**
- Modify: `src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj`
- Modify: `src/Daedalus.AppHost/Daedalus.AppHost.csproj`

**Step 1: Add pgvector EF Core package to Infrastructure**

In `src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj`, add inside the `<ItemGroup>` with other PackageReferences (after the Npgsql.EntityFrameworkCore.PostgreSQL line):

```xml
<PackageReference Include="Pgvector.EntityFrameworkCore" Version="0.3.0" />
```

**Step 2: Add Ollama Aspire hosting package to AppHost**

In `src/Daedalus.AppHost/Daedalus.AppHost.csproj`, add inside the `<ItemGroup>` with other PackageReferences:

```xml
<PackageReference Include="CommunityToolkit.Aspire.Hosting.Ollama" Version="9.4.0" />
```

**Step 3: Verify packages restore**

Run: `dotnet restore src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj && dotnet restore src/Daedalus.AppHost/Daedalus.AppHost.csproj`
Expected: Restore succeeds with no errors.

**Step 4: Commit**

```bash
git add src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj src/Daedalus.AppHost/Daedalus.AppHost.csproj
git commit -m "chore: add pgvector and Ollama Aspire hosting packages"
```

---

### Task 2: Add Embedding Column to Domain Entity

**Files:**
- Modify: `src/Daedalus.Domain/Entities/StructuredLearningEntry.cs`

**Step 1: Add Embedding property to StructuredLearningEntry**

In `src/Daedalus.Domain/Entities/StructuredLearningEntry.cs`, add after the `LastReferencedAt` property (line 44):

```csharp
/// <summary>Gets the vector embedding for semantic search. Null if embedding not yet generated.</summary>
public float[]? Embedding { get; private set; }
```

**Step 2: Add domain method to set embedding**

Add after the `AddTag` method (before the closing brace of the class):

```csharp
/// <summary>
///     Sets the embedding vector for semantic search.
/// </summary>
public void SetEmbedding(float[] embedding)
{
    ArgumentNullException.ThrowIfNull(embedding);
    Embedding = embedding;
}
```

**Step 3: Verify build**

Run: `dotnet build src/Daedalus.Domain/Daedalus.Domain.csproj`
Expected: Build succeeds.

**Step 4: Commit**

```bash
git add src/Daedalus.Domain/Entities/StructuredLearningEntry.cs
git commit -m "feat: add Embedding property to StructuredLearningEntry for semantic search"
```

---

### Task 3: Configure pgvector in EF Core and Create Migration

**Files:**
- Modify: `src/Daedalus.Infrastructure/Persistence/Configurations/StructuredLearningEntryConfiguration.cs`
- Modify: `src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs` (if pgvector extension registration needed)
- Create: EF Core migration (auto-generated)

**Step 1: Add vector column configuration**

In `src/Daedalus.Infrastructure/Persistence/Configurations/StructuredLearningEntryConfiguration.cs`, add `using Pgvector.EntityFrameworkCore;` at the top.

Then add inside the `Configure` method, after the HitCount index (line 52):

```csharp
entity.Property(e => e.Embedding)
    .HasColumnType("vector(384)");

entity.HasIndex(e => e.Embedding)
    .HasDatabaseName("IX_StructuredLearning_Embedding")
    .HasMethod("ivfflat")
    .HasOperators("vector_cosine_ops");
```

**Step 2: Register pgvector with Npgsql**

Check `ApplicationDbContext.cs` or wherever `UseNpgsql` is called. Ensure `UseVector()` is called. In the `AddApplicationDatabase` extension method or wherever `options.UseNpgsql(...)` is configured, add `.UseVector()` to the options builder. Find the exact location — it should be in `AddApplicationDatabase` in `ServiceDefaults` or `InfrastructureServiceExtensions`.

The `UseNpgsql` call needs to include vector support:
```csharp
options.UseNpgsql(connectionString, npgsqlOptions =>
{
    npgsqlOptions.UseVector();
});
```

**Step 3: Create EF Core migration**

Run: `dotnet ef migrations add AddSemanticEmbeddings --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api`

Verify the generated migration contains:
- `CREATE EXTENSION IF NOT EXISTS vector` (or add manually in the Up method)
- `AddColumn` for `Embedding` with type `vector(384)`
- Index creation with ivfflat

**Step 4: Verify build**

Run: `dotnet build src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj`
Expected: Build succeeds.

**Step 5: Commit**

```bash
git add src/Daedalus.Infrastructure/
git commit -m "feat: configure pgvector for semantic embeddings and add migration"
```

---

### Task 4: Create Embedding Service Abstraction and Implementation

**Files:**
- Create: `src/Daedalus.Application/Abstractions/IEmbeddingService.cs`
- Create: `src/Daedalus.Infrastructure/Services/OllamaEmbeddingService.cs`
- Create: `src/Daedalus.Infrastructure/Services/NoOpEmbeddingService.cs`

**Step 1: Create IEmbeddingService interface**

Create `src/Daedalus.Application/Abstractions/IEmbeddingService.cs`:

```csharp
using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Generates vector embeddings for semantic search.
///     Implementations may use Ollama, OpenAI, or a no-op fallback.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    ///     Generates an embedding vector for the given text.
    /// </summary>
    /// <param name="text">Text to embed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Float array of dimension 384, or failure if unavailable.</returns>
    Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct = default);

    /// <summary>
    ///     Whether the embedding service is available and healthy.
    /// </summary>
    bool IsAvailable { get; }
}
```

**Step 2: Create OllamaEmbeddingService**

Create `src/Daedalus.Infrastructure/Services/OllamaEmbeddingService.cs`:

```csharp
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Generates embeddings using Ollama's nomic-embed-text model
///     via the Microsoft.Extensions.AI IEmbeddingGenerator abstraction.
/// </summary>
public sealed partial class OllamaEmbeddingService(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ILogger<OllamaEmbeddingService> logger) : IEmbeddingService
{
    private bool _available = true;

    public bool IsAvailable => _available;

    public async Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Result.Failure<float[]>("Text cannot be empty");
        }

        try
        {
            var result = await embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken: ct);
            _available = true;
            return Result.Success(result.Vector.ToArray());
        }
        catch (Exception ex)
        {
            _available = false;
            LogEmbeddingFailed(logger, ex, text.Length);
            return Result.Failure<float[]>($"Embedding generation failed: {ex.Message}");
        }
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Warning,
        Message = "Embedding generation failed for text of length {TextLength}")]
    private static partial void LogEmbeddingFailed(ILogger logger, Exception exception, int textLength);
}
```

**Step 3: Create NoOpEmbeddingService**

Create `src/Daedalus.Infrastructure/Services/NoOpEmbeddingService.cs`:

```csharp
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     No-op embedding service used when Ollama is unavailable.
///     Returns failure for all embedding requests, triggering keyword search fallback.
/// </summary>
public sealed class NoOpEmbeddingService : IEmbeddingService
{
    public bool IsAvailable => false;

    public Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        return Task.FromResult(Result.Failure<float[]>("Embedding service unavailable — using keyword search fallback"));
    }
}
```

**Step 4: Verify build**

Run: `dotnet build src/Daedalus.Application/Daedalus.Application.csproj && dotnet build src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj`
Expected: Build succeeds.

**Step 5: Commit**

```bash
git add src/Daedalus.Application/Abstractions/IEmbeddingService.cs src/Daedalus.Infrastructure/Services/OllamaEmbeddingService.cs src/Daedalus.Infrastructure/Services/NoOpEmbeddingService.cs
git commit -m "feat: add IEmbeddingService with Ollama and NoOp implementations"
```

---

### Task 5: Add Semantic Search to Learnings Repository

**Files:**
- Modify: `src/Daedalus.Application/Abstractions/ILearningsRepository.cs`
- Modify: `src/Daedalus.Infrastructure/Persistence/LearningsRepository.cs`

**Step 1: Add SemanticSearchAsync to interface**

In `src/Daedalus.Application/Abstractions/ILearningsRepository.cs`, add after the `SearchByTagsAsync` method:

```csharp
/// <summary>
///     Searches learnings by vector similarity using pgvector cosine distance.
///     Falls back to keyword search if embeddings are not available.
/// </summary>
/// <param name="queryEmbedding">The query embedding vector.</param>
/// <param name="projectId">Optional project filter.</param>
/// <param name="maxResults">Maximum results to return.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>Learnings ordered by cosine similarity.</returns>
Task<Result<IReadOnlyList<StructuredLearningEntry>>> SemanticSearchAsync(
    float[] queryEmbedding,
    Guid? projectId,
    int maxResults,
    CancellationToken ct);
```

**Step 2: Implement in LearningsRepository**

In `src/Daedalus.Infrastructure/Persistence/LearningsRepository.cs`, add `using Pgvector.EntityFrameworkCore;` at the top.

Add the implementation after the `SearchByTagsAsync` method:

```csharp
public async Task<Result<IReadOnlyList<StructuredLearningEntry>>> SemanticSearchAsync(
    float[] queryEmbedding, Guid? projectId, int maxResults, CancellationToken ct)
{
    try
    {
        var query = dbContext.StructuredLearnings
            .AsNoTracking()
            .Where(l => l.Embedding != null);

        if (projectId.HasValue)
        {
            query = query.Where(l => l.ProjectId == projectId.Value);
        }

        var results = await query
            .OrderBy(l => l.Embedding!.CosineDistance(queryEmbedding))
            .Take(maxResults)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Result.Success<IReadOnlyList<StructuredLearningEntry>>(results);
    }
    catch (Exception ex)
    {
        LogSearchFailed(logger, ex, "semantic", "vector");
        return Result.Failure<IReadOnlyList<StructuredLearningEntry>>(
            $"Semantic search failed: {ex.Message}");
    }
}
```

**Step 3: Verify build**

Run: `dotnet build src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj`
Expected: Build succeeds.

**Step 4: Commit**

```bash
git add src/Daedalus.Application/Abstractions/ILearningsRepository.cs src/Daedalus.Infrastructure/Persistence/LearningsRepository.cs
git commit -m "feat: add semantic vector search to ILearningsRepository"
```

---

### Task 6: Embed Learnings on Save

**Files:**
- Modify: `src/Daedalus.Application/Services/LearningsService.cs` (or wherever `ParseAndPersistLearningsAsync` is implemented)

**Step 1: Inject IEmbeddingService into LearningsService**

Add `IEmbeddingService embeddingService` to the LearningsService constructor parameters.

**Step 2: Generate embedding when persisting a learning entry**

After each `StructuredLearningEntry.Create(...)` call, before `AddAsync`, generate and set the embedding:

```csharp
// Generate embedding for semantic search (non-fatal if fails)
var embeddingText = $"{entry.Pattern} {entry.Resolution}";
var embeddingResult = await embeddingService.GenerateEmbeddingAsync(embeddingText, ct);
if (embeddingResult.IsSuccess)
{
    entry.SetEmbedding(embeddingResult.Value);
}
```

**Step 3: Verify build**

Run: `dotnet build src/Daedalus.Application/Daedalus.Application.csproj`
Expected: Build succeeds.

**Step 4: Commit**

```bash
git add src/Daedalus.Application/Services/LearningsService.cs
git commit -m "feat: generate embeddings when persisting structured learnings"
```

---

### Task 7: Create MCP Tool Classes

**Files:**
- Create: `src/Daedalus.Infrastructure/Agents/Tools/DaedalusLearningsTools.cs`
- Create: `src/Daedalus.Infrastructure/Agents/Tools/DaedalusFailurePatternsTools.cs`

**Step 1: Create the learnings search tool**

Create `src/Daedalus.Infrastructure/Agents/Tools/DaedalusLearningsTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Daedalus.Infrastructure.Agents.Tools;

/// <summary>
///     MCP tools for searching structured learnings from the knowledge base.
///     Used by the Ralph Loop LLM to query relevant learnings on-demand.
/// </summary>
[McpServerToolType]
public sealed partial class DaedalusLearningsTools(
    ILearningsRepository learningsRepository,
    IEmbeddingService embeddingService,
    ILogger<DaedalusLearningsTools> logger)
{
    [McpServerTool(
        Name = "search_learnings",
        ReadOnly = true,
        Idempotent = true)]
    [Description(
        "Search past learnings from previous task executions using semantic similarity. " +
        "Use this when you encounter errors, need context about the codebase, or want to " +
        "learn from previous approaches that worked or failed.")]
    public async Task<string> SearchLearnings(
        [Description("Natural language description of what you're looking for")] string query,
        [Description("Filter to learnings from a specific project (optional)")] string? projectId = null,
        [Description("Maximum number of results (default: 5)")] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid? parsedProjectId = null;
            if (!string.IsNullOrEmpty(projectId) && Guid.TryParse(projectId, out var parsed))
            {
                parsedProjectId = parsed;
            }

            // Try semantic search first
            if (embeddingService.IsAvailable)
            {
                var embeddingResult = await embeddingService.GenerateEmbeddingAsync(query, cancellationToken);
                if (embeddingResult.IsSuccess)
                {
                    var semanticResult = await learningsRepository.SemanticSearchAsync(
                        embeddingResult.Value, parsedProjectId, maxResults, cancellationToken);

                    if (semanticResult.IsSuccess && semanticResult.Value.Count > 0)
                    {
                        LogSemanticSearchUsed(logger, query, semanticResult.Value.Count);
                        return FormatResults(semanticResult.Value);
                    }
                }
            }

            // Fallback: keyword search
            LogKeywordFallback(logger, query);
            var keywordResult = await learningsRepository.SearchByKeywordAsync(
                query, maxResults, cancellationToken);

            if (keywordResult.IsSuccess && keywordResult.Value.Count > 0)
            {
                return FormatResults(keywordResult.Value);
            }

            return "No matching learnings found.";
        }
        catch (Exception ex)
        {
            LogSearchError(logger, ex, query);
            return $"Error searching learnings: {ex.Message}. Proceed with available context.";
        }
    }

    private static string FormatResults(IReadOnlyList<Domain.Entities.StructuredLearningEntry> entries)
    {
        var results = entries.Select(e => new
        {
            category = e.Category.ToString(),
            pattern = e.Pattern,
            resolution = e.Resolution,
            severity = e.Severity.ToString(),
            hitCount = e.HitCount,
            createdAt = e.CreatedAt.ToString("yyyy-MM-dd")
        });

        return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Debug,
        Message = "Semantic search used for query '{Query}', found {Count} results")]
    private static partial void LogSemanticSearchUsed(ILogger logger, string query, int count);

    [LoggerMessage(EventId = 401, Level = LogLevel.Debug,
        Message = "Falling back to keyword search for query '{Query}'")]
    private static partial void LogKeywordFallback(ILogger logger, string query);

    [LoggerMessage(EventId = 402, Level = LogLevel.Warning,
        Message = "Error searching learnings for query '{Query}'")]
    private static partial void LogSearchError(ILogger logger, Exception exception, string query);
}
```

**Step 2: Create the failure patterns search tool**

Create `src/Daedalus.Infrastructure/Agents/Tools/DaedalusFailurePatternsTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Daedalus.Infrastructure.Agents.Tools;

/// <summary>
///     MCP tools for searching known failure patterns and their solutions.
///     Used by the Ralph Loop LLM to find relevant fixes when encountering errors.
/// </summary>
[McpServerToolType]
public sealed partial class DaedalusFailurePatternsTools(
    IFailurePatternDatabase failurePatternDatabase,
    ILogger<DaedalusFailurePatternsTools> logger)
{
    [McpServerTool(
        Name = "search_failure_patterns",
        ReadOnly = true,
        Idempotent = true)]
    [Description(
        "Search known failure patterns and their solutions. Use this when you encounter " +
        "a build error, test failure, or runtime exception to find previously discovered fixes.")]
    public async Task<string> SearchFailurePatterns(
        [Description("The error message or pattern to search for")] string errorMessage,
        [Description("Maximum number of results (default: 3)")] int maxResults = 3,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await failurePatternDatabase.SearchByErrorAsync(
                errorMessage, maxResults, cancellationToken);

            if (result.IsSuccess && result.Value.Count > 0)
            {
                LogPatternsFound(logger, errorMessage, result.Value.Count);
                return FormatResults(result.Value);
            }

            return "No matching failure patterns found.";
        }
        catch (Exception ex)
        {
            LogSearchError(logger, ex, errorMessage);
            return $"Error searching failure patterns: {ex.Message}. Proceed with available context.";
        }
    }

    private static string FormatResults(IReadOnlyList<FailurePatternRecord> patterns)
    {
        var results = patterns.Select(p => new
        {
            error = p.ErrorText.Length > 300 ? p.ErrorText[..300] + "..." : p.ErrorText,
            solution = p.Resolution,
            sourceTaskId = p.SourceTaskId,
            errorIteration = p.ErrorIteration,
            resolutionIteration = p.ResolutionIteration,
            observedAt = p.ObservedAt.ToString("yyyy-MM-dd")
        });

        return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    }

    [LoggerMessage(EventId = 410, Level = LogLevel.Debug,
        Message = "Found {Count} failure patterns for error '{ErrorMessage}'")]
    private static partial void LogPatternsFound(ILogger logger, string errorMessage, int count);

    [LoggerMessage(EventId = 411, Level = LogLevel.Warning,
        Message = "Error searching failure patterns for '{ErrorMessage}'")]
    private static partial void LogSearchError(ILogger logger, Exception exception, string errorMessage);
}
```

**Step 3: Verify build**

Run: `dotnet build src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj`
Expected: Build succeeds.

**Step 4: Commit**

```bash
git add src/Daedalus.Infrastructure/Agents/Tools/
git commit -m "feat: add MCP tool classes for learnings and failure pattern search"
```

---

### Task 8: Add Local Server Type to McpToolBuilder

**Files:**
- Modify: `src/Daedalus.Infrastructure/Agents/McpToolBuilder.cs`

**Step 1: Add a DI-based local tool provider**

The `McpToolBuilder` needs to resolve `McpServerTool` instances from DI for `"local"` type servers. Add an `IServiceProvider` parameter to the constructor and a method to build local tools.

Update the constructor at line 21 to accept `IServiceProvider`:

```csharp
public sealed partial class McpToolBuilder(
    IServiceProvider serviceProvider,
    ILogger<McpToolBuilder> logger) : IAsyncDisposable
```

**Step 2: Add local tool resolution in GetOrConnectServerToolsAsync**

In `GetOrConnectServerToolsAsync`, before the `CreateTransport` call (line 76), add a check for local type:

```csharp
// Handle local (in-process) tool type
if (serverConfig.Type.Equals("local", StringComparison.OrdinalIgnoreCase))
{
    var localTools = BuildLocalTools(serverName);
    _toolCache[serverName] = localTools;
    LogServerConnected(logger, serverName, localTools.Count);
    return localTools;
}
```

**Step 3: Add BuildLocalTools method**

Add a private method that uses `McpServerTool.Create` to build tools from DI-resolved tool class instances:

```csharp
/// <summary>
///     Builds tools from in-process [McpServerToolType] classes resolved via DI.
/// </summary>
private IReadOnlyList<AITool> BuildLocalTools(string serverName)
{
    var tools = new List<AITool>();

    // Discover and instantiate tool classes from the Infrastructure assembly
    var toolTypes = GetType().Assembly
        .GetTypes()
        .Where(t => t.GetCustomAttributes(typeof(McpServerToolType), false).Length > 0
                     && !t.IsAbstract);

    foreach (var toolType in toolTypes)
    {
        try
        {
            var instance = ActivatorUtilities.CreateInstance(serviceProvider, toolType);
            var methods = toolType.GetMethods()
                .Where(m => m.GetCustomAttributes(typeof(McpServerTool), false).Length > 0);

            foreach (var method in methods)
            {
                var tool = McpServerTool.Create(method, instance);
                tools.Add(tool);
            }
        }
        catch (Exception ex)
        {
            LogServerConnectionFailed(logger, ex, serverName,
                $"Failed to create local tool from {toolType.Name}: {ex.Message}");
        }
    }

    return tools;
}
```

Add these usings at the top of the file:
```csharp
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
```

**Step 4: Verify build**

Run: `dotnet build src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj`
Expected: Build succeeds.

**Step 5: Commit**

```bash
git add src/Daedalus.Infrastructure/Agents/McpToolBuilder.cs
git commit -m "feat: add local server type to McpToolBuilder for in-process MCP tools"
```

---

### Task 9: Modify LearningsEnrichmentMiddleware for Conditional Mode

**Files:**
- Modify: `src/Daedalus.Application/Services/Middleware/LearningsEnrichmentMiddleware.cs`

**Step 1: Add tool availability detection**

The middleware needs to know if MCP tools are available. Add a constructor parameter for a flag or service that indicates local MCP tool status. The simplest approach: add a boolean configuration or check via a new `IKnowledgeBaseToolStatus` interface.

Create a simple interface in `src/Daedalus.Application/Abstractions/IKnowledgeBaseToolStatus.cs`:

```csharp
namespace Daedalus.Application.Abstractions;

/// <summary>
///     Indicates whether the knowledge base MCP tools are available.
///     When available, LearningsEnrichmentMiddleware uses slim mode (summary only).
///     When unavailable, falls back to full text injection.
/// </summary>
public interface IKnowledgeBaseToolStatus
{
    /// <summary>Whether the search_learnings and search_failure_patterns tools are registered.</summary>
    bool AreToolsAvailable { get; }

    /// <summary>The count of available learnings in the knowledge base.</summary>
    int LearningsCount { get; }

    /// <summary>The count of known failure patterns.</summary>
    int FailurePatternsCount { get; }
}
```

**Step 2: Update the middleware**

Modify `LearningsEnrichmentMiddleware` to accept `IKnowledgeBaseToolStatus` and switch modes:

```csharp
public sealed partial class LearningsEnrichmentMiddleware(
    ILearningsService learningsService,
    IKnowledgeBaseToolStatus toolStatus,
    ILogger<LearningsEnrichmentMiddleware> logger) : IRalphLoopMiddleware
{
    private const int _maxLearnings = 10;
    private const int _maxFailurePatterns = 5;

    public int Order => 90;

    public async Task<Result> InvokeAsync(
        RalphIterationContext context,
        Func<Task<Result>> continuation,
        CancellationToken ct)
    {
        try
        {
            if (context.Iteration != 1 && context.Iteration % 3 != 0)
            {
                return await continuation();
            }

            if (toolStatus.AreToolsAvailable)
            {
                // Slim mode: inject summary + tool usage hint
                var summary = $"=== KNOWLEDGE BASE ===\n" +
                    $"You have access to a knowledge base with {toolStatus.LearningsCount} learnings " +
                    $"and {toolStatus.FailurePatternsCount} failure patterns.\n" +
                    $"Use the search_learnings tool to find relevant past knowledge.\n" +
                    $"Use the search_failure_patterns tool when you encounter errors.\n";

                context.PromptContext.AccumulatedLearnings = summary;
                LogSlimEnrichment(logger, context.Iteration, toolStatus.LearningsCount);
            }
            else
            {
                // Fallback: full text injection (current behavior)
                var enrichmentResult = await learningsService.GetEnrichmentContextAsync(
                    context.Task.Prompt,
                    context.Task.ProjectId != Guid.Empty ? context.Task.ProjectId : null,
                    context.Task.Id,
                    _maxLearnings,
                    _maxFailurePatterns,
                    ct);

                if (enrichmentResult.IsSuccess && !string.IsNullOrEmpty(enrichmentResult.Value))
                {
                    var existingLearnings = context.PromptContext.AccumulatedLearnings ?? string.Empty;
                    context.PromptContext.AccumulatedLearnings = string.IsNullOrEmpty(existingLearnings)
                        ? enrichmentResult.Value
                        : $"{existingLearnings}\n\n{enrichmentResult.Value}";

                    LogEnrichmentInjected(logger, context.Iteration, enrichmentResult.Value.Length);
                }
                else if (enrichmentResult.IsFailure)
                {
                    LogEnrichmentFailed(logger, context.Iteration, enrichmentResult.Error);
                }
                else
                {
                    LogNoEnrichment(logger, context.Iteration);
                }
            }

            return await continuation();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Unexpected error during learnings enrichment at iteration {Iteration}",
                context.Iteration);
            return await continuation();
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Debug,
        Message = "Learnings enrichment injected for iteration {Iteration}, enrichment length: {Length}")]
    private static partial void LogEnrichmentInjected(ILogger logger, int iteration, int length);

    [LoggerMessage(EventId = 101, Level = LogLevel.Debug,
        Message = "No enrichment context available for iteration {Iteration}")]
    private static partial void LogNoEnrichment(ILogger logger, int iteration);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning,
        Message = "Learnings enrichment failed for iteration {Iteration}: {Error}")]
    private static partial void LogEnrichmentFailed(ILogger logger, int iteration, string error);

    [LoggerMessage(EventId = 103, Level = LogLevel.Debug,
        Message = "Slim enrichment mode for iteration {Iteration}: {LearningsCount} learnings available via MCP tools")]
    private static partial void LogSlimEnrichment(ILogger logger, int iteration, int learningsCount);
}
```

**Step 3: Verify build**

Run: `dotnet build src/Daedalus.Application/Daedalus.Application.csproj`
Expected: Build succeeds.

**Step 4: Commit**

```bash
git add src/Daedalus.Application/Abstractions/IKnowledgeBaseToolStatus.cs src/Daedalus.Application/Services/Middleware/LearningsEnrichmentMiddleware.cs
git commit -m "feat: add conditional slim/full mode to LearningsEnrichmentMiddleware"
```

---

### Task 10: Register Services and Configure Aspire

**Files:**
- Modify: `src/Daedalus.Infrastructure/Extensions/InfrastructureServiceExtensions.cs`
- Modify: `src/Daedalus.AppHost/Program.cs`
- Modify: `src/Daedalus.Api/appsettings.json`
- Create: `src/Daedalus.Infrastructure/Services/KnowledgeBaseToolStatus.cs`

**Step 1: Create KnowledgeBaseToolStatus implementation**

Create `src/Daedalus.Infrastructure/Services/KnowledgeBaseToolStatus.cs`:

```csharp
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Reports whether MCP knowledge base tools are available and cached counts.
/// </summary>
public sealed partial class KnowledgeBaseToolStatus(
    McpIntegrationOptions mcpOptions,
    ApplicationDbContext dbContext,
    ILogger<KnowledgeBaseToolStatus> logger) : IKnowledgeBaseToolStatus
{
    private int? _learningsCount;
    private int? _failurePatternsCount;

    public bool AreToolsAvailable =>
        mcpOptions.Enabled &&
        mcpOptions.Servers.ContainsKey("daedalus-knowledge");

    public int LearningsCount
    {
        get
        {
            if (_learningsCount.HasValue) return _learningsCount.Value;
            try
            {
                _learningsCount = dbContext.StructuredLearnings.Count();
            }
            catch
            {
                _learningsCount = 0;
            }
            return _learningsCount.Value;
        }
    }

    public int FailurePatternsCount
    {
        get
        {
            if (_failurePatternsCount.HasValue) return _failurePatternsCount.Value;
            try
            {
                _failurePatternsCount = dbContext.TaskExecutions.Count(e => e.Error != null);
            }
            catch
            {
                _failurePatternsCount = 0;
            }
            return _failurePatternsCount.Value;
        }
    }
}
```

**Step 2: Register services in InfrastructureServiceExtensions**

In `src/Daedalus.Infrastructure/Extensions/InfrastructureServiceExtensions.cs`, in the `AddExternalServices` method, after the failure pattern database registration (line 79), add:

```csharp
// Register knowledge base tool status for LearningsEnrichmentMiddleware mode switching
services.AddScoped<IKnowledgeBaseToolStatus, KnowledgeBaseToolStatus>();
```

In the `AddAgentFrameworkServices` method, after the McpToolBuilder registration (line 94), add:

```csharp
// Register embedding service — try Ollama, fallback to NoOp
// The actual IEmbeddingGenerator<string, Embedding<float>> must be registered
// by the hosting project (API/Console) based on Ollama availability
services.AddScoped<IEmbeddingService>(sp =>
{
    var generator = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
    if (generator != null)
    {
        return new OllamaEmbeddingService(
            generator,
            sp.GetRequiredService<ILogger<OllamaEmbeddingService>>());
    }
    return new NoOpEmbeddingService();
});
```

Add the required usings:
```csharp
using Microsoft.Extensions.AI;
```

**Step 3: Add daedalus-knowledge to appsettings.json**

In `src/Daedalus.Api/appsettings.json`, in the `ExternalServices.Mcp.Servers` section, add after context7:

```json
"daedalus-knowledge": {
  "Type": "local",
  "Tools": ["*"]
}
```

**Step 4: Configure Ollama in AppHost**

In `src/Daedalus.AppHost/Program.cs`, add the Ollama container after the Keycloak configuration (around line 50):

```csharp
// Configure Ollama for embedding generation (semantic search)
var ollama = builder.AddOllama("ollama")
    .WithDataVolume()
    .AddModel("nomic-embed-text");
```

Then update the API project reference to include Ollama:

```csharp
var api = builder.AddProject("api", apiPath)
    .WithReference(database)
    .WithReference(keycloak)
    .WithReference(migrations)
    .WithReference(ollama)
    // ... rest stays the same
```

**Step 5: Register IEmbeddingGenerator in API Program.cs**

In `src/Daedalus.Api/Program.cs`, after the agent framework services registration (line 50), add:

```csharp
// Register Ollama embedding generator for semantic search
// Falls back gracefully via NoOpEmbeddingService if Ollama is unavailable
builder.Services.AddEmbeddingGenerator(b => b
    .Use(new OllamaEmbeddingGenerator(
        new Uri(builder.Configuration["ConnectionStrings:ollama"] ?? "http://localhost:11434"),
        "nomic-embed-text")));
```

Note: The exact registration depends on the Ollama .NET client available. If `OllamaEmbeddingGenerator` doesn't exist as a concrete class, use `Microsoft.Extensions.AI.Ollama` package's registration method. Check the exact API at implementation time.

**Step 6: Verify build**

Run: `dotnet build`
Expected: Build succeeds.

**Step 7: Commit**

```bash
git add src/Daedalus.Infrastructure/Services/KnowledgeBaseToolStatus.cs src/Daedalus.Infrastructure/Extensions/InfrastructureServiceExtensions.cs src/Daedalus.Api/appsettings.json src/Daedalus.AppHost/Program.cs src/Daedalus.Api/Program.cs
git commit -m "feat: register MCP knowledge base services and configure Ollama in Aspire"
```

---

### Task 11: Fix Test Compilation Errors

**Files:**
- Modify: Any test files that mock `ILearningsRepository` (add `SemanticSearchAsync` stub)
- Modify: Any test files that create `LearningsEnrichmentMiddleware` (add `IKnowledgeBaseToolStatus` parameter)

**Step 1: Find and fix all test compilation errors**

Run: `dotnet build` and identify all test projects that fail.

For tests that mock `ILearningsRepository`: add a setup for the new `SemanticSearchAsync` method.

For tests that create `LearningsEnrichmentMiddleware`: add a mock for `IKnowledgeBaseToolStatus` with `AreToolsAvailable = false` to maintain existing behavior in tests.

**Step 2: Verify all tests pass**

Run: `dotnet test`
Expected: All existing tests pass (no regressions).

**Step 3: Commit**

```bash
git add -A
git commit -m "fix: update tests for ILearningsRepository and LearningsEnrichmentMiddleware changes"
```

---

### Task 12: Build Verification and Integration Test

**Files:**
- Verify: Full solution build
- Run: Full test suite

**Step 1: Full solution build**

Run: `dotnet build`
Expected: 0 errors. Warnings about MSB3277 (EntityFrameworkCore version conflict) are acceptable (pre-existing).

**Step 2: Run all tests**

Run: `dotnet test`
Expected: All tests pass (644+ tests).

**Step 3: Commit any remaining fixes**

If any test failures, fix and commit with descriptive message.

---
