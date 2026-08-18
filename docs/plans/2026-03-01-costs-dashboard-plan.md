# Costs Dashboard Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `/costs` page that tracks actual LLM token usage per execution, calculates costs via configurable model pricing, and provides a cost estimator for planning Ralph runs.

**Architecture:** Extend TaskExecution and AnalysisIteration entities with token/model fields, introduce a new `LlmInvocationResult` return type from the agent factory to carry token data through the pipeline, add a `CostAnalyticsService` for aggregate queries, expose via a new `CostAnalyticsController`, and render in a Blazor Radzen page.

**Tech Stack:** .NET 10, EF Core 10 (PostgreSQL), Blazor WASM, Radzen components, CSharpFunctionalExtensions

---

## Task 1: Add Token Fields to Domain Entities

**Files:**
- Modify: `src/Daedalus.Domain/Entities/TaskExecution.cs`
- Modify: `src/Daedalus.Domain/CodeAnalysis/AnalysisIteration.cs`

**Step 1: Add properties to TaskExecution**

Add three new properties after `Error` (line 33) in `src/Daedalus.Domain/Entities/TaskExecution.cs`:

```csharp
/// <summary>Gets the number of input tokens consumed by this invocation.</summary>
public int InputTokens { get; init; }

/// <summary>Gets the number of output tokens produced by this invocation.</summary>
public int OutputTokens { get; init; }

/// <summary>Gets the model ID used for this invocation (e.g., "claude-sonnet-4-20250514").</summary>
public string? ModelId { get; init; }
```

**Step 2: Add properties to AnalysisIteration**

Add three new properties after `CreatedAt` (line 28) in `src/Daedalus.Domain/CodeAnalysis/AnalysisIteration.cs`:

```csharp
// Token usage
public int InputTokens { get; private set; }
public int OutputTokens { get; private set; }
public string? ModelId { get; private set; }
```

Update the `Create` factory method signature to accept optional token parameters:

```csharp
public static Result<AnalysisIteration> Create(
    Guid id,
    Guid codeAnalysisRequestId,
    int iterationNumber,
    string promptSent,
    string aiResponse,
    string? branchName = null,
    string? commitSha = null,
    int inputTokens = 0,
    int outputTokens = 0,
    string? modelId = null)
```

And set them in the object initializer inside Create:
```csharp
InputTokens = inputTokens,
OutputTokens = outputTokens,
ModelId = modelId?.Trim()
```

**Step 3: Build to verify no compilation errors**

Run: `dotnet build src/Daedalus.Domain/Daedalus.Domain.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Daedalus.Domain/Entities/TaskExecution.cs src/Daedalus.Domain/CodeAnalysis/AnalysisIteration.cs
git commit -m "feat: add InputTokens, OutputTokens, ModelId to TaskExecution and AnalysisIteration"
```

---

## Task 2: Update EF Core Configurations and Create Migration

**Files:**
- Modify: `src/Daedalus.Infrastructure/Persistence/Configurations/TaskExecutionConfiguration.cs`
- Modify: `src/Daedalus.Infrastructure/Persistence/Configurations/AnalysisIterationConfiguration.cs`

**Step 1: Update TaskExecutionConfiguration**

Add after the `Error` property config (line 25) in `TaskExecutionConfiguration.cs`:

```csharp
entity.Property(e => e.InputTokens)
    .HasDefaultValue(0);

entity.Property(e => e.OutputTokens)
    .HasDefaultValue(0);

entity.Property(e => e.ModelId)
    .HasMaxLength(100);
```

**Step 2: Update AnalysisIterationConfiguration**

Add after the `ValidationErrors` config (line 29) in `AnalysisIterationConfiguration.cs`:

```csharp
entity.Property(e => e.InputTokens)
    .HasDefaultValue(0);

entity.Property(e => e.OutputTokens)
    .HasDefaultValue(0);

entity.Property(e => e.ModelId)
    .HasMaxLength(100);
```

**Step 3: Create EF Core migration**

Run: `dotnet ef migrations add AddTokenUsageColumns --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Migrations`
Expected: Migration file created in `src/Daedalus.Infrastructure/Migrations/`

**Step 4: Build to verify**

Run: `dotnet build src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Daedalus.Infrastructure/Persistence/Configurations/ src/Daedalus.Infrastructure/Migrations/
git commit -m "feat: add EF Core migration for token usage columns"
```

---

## Task 3: Create LlmInvocationResult and Update IRalphAgentFactory Interface

**Files:**
- Create: `src/Daedalus.Application/Abstractions/LlmInvocationResult.cs`
- Modify: `src/Daedalus.Application/Abstractions/IRalphAgentFactory.cs`

**Step 1: Create LlmInvocationResult**

Create `src/Daedalus.Application/Abstractions/LlmInvocationResult.cs`:

```csharp
namespace Daedalus.Application.Abstractions;

/// <summary>
///     Result of a primary LLM invocation, including response text and token usage.
/// </summary>
public sealed class LlmInvocationResult
{
    /// <summary>The LLM response text.</summary>
    public required string Response { get; init; }

    /// <summary>Number of input tokens consumed.</summary>
    public int InputTokens { get; init; }

    /// <summary>Number of output tokens produced.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Model ID used for this invocation.</summary>
    public string? ModelId { get; init; }
}
```

**Step 2: Update IRalphAgentFactory**

In `src/Daedalus.Application/Abstractions/IRalphAgentFactory.cs`, change the `InvokeAsync` return type (line 27):

From:
```csharp
Task<Result<string>> InvokeAsync(
    string prompt,
    CancellationToken ct = default);
```

To:
```csharp
Task<Result<LlmInvocationResult>> InvokeAsync(
    string prompt,
    CancellationToken ct = default);
```

**Step 3: Build to verify — expect compilation errors in consumers**

Run: `dotnet build src/Daedalus.Application/Daedalus.Application.csproj`
Expected: Build errors in `LlmInvocationMiddleware.cs` (will fix in next task)

**Step 4: Commit**

```bash
git add src/Daedalus.Application/Abstractions/LlmInvocationResult.cs src/Daedalus.Application/Abstractions/IRalphAgentFactory.cs
git commit -m "feat: introduce LlmInvocationResult type with token usage data"
```

---

## Task 4: Update RalphAgentFactory to Return Token Data

**Files:**
- Modify: `src/Daedalus.Infrastructure/Agents/RalphAgentFactory.cs`

**Step 1: Update InvokeAsync to return LlmInvocationResult**

In `RalphAgentFactory.cs`, change the `InvokeAsync` method (lines 59-116):

Change signature (line 59):
```csharp
public async Task<Result<LlmInvocationResult>> InvokeAsync(string prompt, CancellationToken ct = default)
```

Change the success return (around line 103-104), from:
```csharp
LogInvocationCompleted(_logger, text.Length);
return Result.Success(text);
```
To:
```csharp
var (inputTokens, outputTokens) = ExtractTokenUsage(response);
LogInvocationCompleted(_logger, text.Length);
return Result.Success(new LlmInvocationResult
{
    Response = text,
    InputTokens = inputTokens,
    OutputTokens = outputTokens,
    ModelId = _defaultModel
});
```

Change failure returns from `Result.Failure<string>(...)` to `Result.Failure<LlmInvocationResult>(...)` at lines 63, 68, 100, 109, 114.

**Step 2: Build to verify infrastructure compiles**

Run: `dotnet build src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Daedalus.Infrastructure/Agents/RalphAgentFactory.cs
git commit -m "feat: return token usage from primary InvokeAsync path"
```

---

## Task 5: Add Token Fields to RalphIterationContext and Update Middleware

**Files:**
- Modify: `src/Daedalus.Application/Abstractions/RalphIterationContext.cs`
- Modify: `src/Daedalus.Application/Services/Middleware/LlmInvocationMiddleware.cs`

**Step 1: Add token fields to RalphIterationContext**

Add after `InvocationDuration` (line 53) in `RalphIterationContext.cs`:

```csharp
/// <summary>
///     Number of input tokens consumed by this iteration's LLM invocation.
///     Populated by LlmInvocationMiddleware.
/// </summary>
public int InputTokens { get; set; }

/// <summary>
///     Number of output tokens produced by this iteration's LLM invocation.
///     Populated by LlmInvocationMiddleware.
/// </summary>
public int OutputTokens { get; set; }

/// <summary>
///     The model ID used for this iteration's LLM invocation.
///     Populated by LlmInvocationMiddleware.
/// </summary>
public string? ModelId { get; set; }
```

**Step 2: Update LlmInvocationMiddleware to extract token data**

In `LlmInvocationMiddleware.cs`, update the success path (lines 48-51):

From:
```csharp
context.LlmResponse = invokeResult.Value;
context.LlmInvocationSucceeded = true;
context.ConsecutiveFailures = 0;
```

To:
```csharp
var result = invokeResult.Value;
context.LlmResponse = result.Response;
context.InputTokens = result.InputTokens;
context.OutputTokens = result.OutputTokens;
context.ModelId = result.ModelId;
context.LlmInvocationSucceeded = true;
context.ConsecutiveFailures = 0;
```

**Step 3: Build the application layer**

Run: `dotnet build src/Daedalus.Application/Daedalus.Application.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Daedalus.Application/Abstractions/RalphIterationContext.cs src/Daedalus.Application/Services/Middleware/LlmInvocationMiddleware.cs
git commit -m "feat: propagate token usage through iteration context and middleware"
```

---

## Task 6: Persist Token Data in TaskExecution Records

**Files:**
- Modify: `src/Daedalus.Application/Services/RalphLoopPipelineService.cs`
- Modify: `src/Daedalus.Application/DTOs/TaskExecutionDto.cs`

**Step 1: Add token fields to TaskExecution creation**

In `RalphLoopPipelineService.cs`, update the TaskExecution object initializer (lines 259-270):

Add after `CompletionPromiseFound` (line 269):
```csharp
InputTokens = context.InputTokens,
OutputTokens = context.OutputTokens,
ModelId = context.ModelId
```

**Step 2: Update TaskExecutionDto**

In `src/Daedalus.Application/DTOs/TaskExecutionDto.cs`, add three new fields:

```csharp
public record TaskExecutionDto(
    Guid Id,
    Guid TaskId,
    Guid SessionId,
    int IterationNumber,
    string Prompt,
    string LlmResponse,
    bool CompletionPromiseFound,
    DateTime ExecutedAt,
    TimeSpan ExecutionDuration,
    string? Error,
    int InputTokens,
    int OutputTokens,
    string? ModelId);
```

**Step 3: Update any DTO mapping code**

Search for where `TaskExecutionDto` is constructed (likely in a query service or mapping extension). Update the mapping to include the three new fields.

Run: `grep -rn "new TaskExecutionDto" src/` to find all construction sites.

**Step 4: Build the full solution**

Run: `dotnet build`
Expected: Build succeeded (fix any remaining mapping issues)

**Step 5: Commit**

```bash
git add src/Daedalus.Application/Services/RalphLoopPipelineService.cs src/Daedalus.Application/DTOs/TaskExecutionDto.cs
git commit -m "feat: persist token usage in TaskExecution records and DTO"
```

---

## Task 7: Create ModelPricingConfiguration

**Files:**
- Create: `src/Daedalus.Application/Configuration/ModelPricingConfiguration.cs`
- Modify: `src/Daedalus.Api/appsettings.json`

**Step 1: Create the configuration class**

Create `src/Daedalus.Application/Configuration/ModelPricingConfiguration.cs`:

```csharp
namespace Daedalus.Application.Configuration;

/// <summary>
///     Configuration for LLM model pricing used in cost calculations.
/// </summary>
public sealed class ModelPricingConfiguration
{
    public const string SectionName = "ModelPricing";

    /// <summary>
    ///     Per-model pricing keyed by model ID (e.g., "claude-sonnet-4-20250514").
    /// </summary>
    public Dictionary<string, ModelPricing> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
///     Pricing for a specific LLM model.
/// </summary>
public sealed class ModelPricing
{
    /// <summary>Human-readable model name (e.g., "Claude Sonnet 4").</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Cost per 1 million input tokens in USD.</summary>
    public decimal InputTokenPricePerMillion { get; set; }

    /// <summary>Cost per 1 million output tokens in USD.</summary>
    public decimal OutputTokenPricePerMillion { get; set; }
}
```

**Step 2: Add pricing section to appsettings.json**

In `src/Daedalus.Api/appsettings.json`, add after the `RalphLoop` section (after line 31):

```json
"ModelPricing": {
  "Models": {
    "claude-sonnet-4-20250514": {
      "DisplayName": "Claude Sonnet 4",
      "InputTokenPricePerMillion": 3.00,
      "OutputTokenPricePerMillion": 15.00
    },
    "claude-haiku-4-5-20251001": {
      "DisplayName": "Claude Haiku 4.5",
      "InputTokenPricePerMillion": 0.80,
      "OutputTokenPricePerMillion": 4.00
    },
    "claude-opus-4-20250514": {
      "DisplayName": "Claude Opus 4",
      "InputTokenPricePerMillion": 15.00,
      "OutputTokenPricePerMillion": 75.00
    }
  }
},
```

**Step 3: Register configuration in Program.cs**

In `src/Daedalus.Api/Program.cs`, add after the RalphLoop configuration (after line 37):

```csharp
// Register model pricing configuration
builder.Services.Configure<ModelPricingConfiguration>(options =>
    builder.Configuration.GetSection(ModelPricingConfiguration.SectionName).Bind(options));
```

Add the using at the top if needed:
```csharp
using Daedalus.Application.Configuration;
```
(Note: check if `RalphLoopConfiguration` already has this using — it likely does from line 5.)

**Step 4: Build to verify**

Run: `dotnet build src/Daedalus.Api/Daedalus.Api.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Daedalus.Application/Configuration/ModelPricingConfiguration.cs src/Daedalus.Api/appsettings.json src/Daedalus.Api/Program.cs
git commit -m "feat: add configurable model pricing configuration"
```

---

## Task 8: Create Cost Analytics DTOs

**Files:**
- Create: `src/Daedalus.Application/DTOs/CostAnalyticsDtos.cs`

**Step 1: Create the DTOs file**

Create `src/Daedalus.Application/DTOs/CostAnalyticsDtos.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;

namespace Daedalus.Application.DTOs;

/// <summary>Overall cost summary across all projects.</summary>
public record CostSummaryDto(
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalCost,
    int TotalExecutions,
    int TotalTasks);

/// <summary>Cost breakdown for a single project.</summary>
[SuppressMessage("Design", "CA1056", Justification = "DTO uses string for JSON serialization")]
public record ProjectCostDto(
    Guid ProjectId,
    string ProjectName,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    int ExecutionCount);

/// <summary>Cost breakdown for a single task within a project.</summary>
public record TaskCostDto(
    Guid TaskId,
    string TaskTitle,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    int IterationCount);

/// <summary>Estimated cost for a planned Ralph run.</summary>
public record CostEstimateDto(
    string ModelId,
    string ModelDisplayName,
    int MaxIterations,
    int EstimatedPromptTokens,
    int EstimatedResponseTokens,
    decimal EstimatedMinCost,
    decimal EstimatedMaxCost);

/// <summary>Model pricing information for UI display.</summary>
public record ModelPricingDto(
    string ModelId,
    string DisplayName,
    decimal InputPricePerMillion,
    decimal OutputPricePerMillion);
```

**Step 2: Build to verify**

Run: `dotnet build src/Daedalus.Application/Daedalus.Application.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Daedalus.Application/DTOs/CostAnalyticsDtos.cs
git commit -m "feat: add cost analytics DTOs"
```

---

## Task 9: Create ICostAnalyticsService Interface

**Files:**
- Create: `src/Daedalus.Application/Abstractions/ICostAnalyticsService.cs`

**Step 1: Create the interface**

Create `src/Daedalus.Application/Abstractions/ICostAnalyticsService.cs`:

```csharp
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Service for querying cost analytics data across projects and executions.
/// </summary>
public interface ICostAnalyticsService
{
    /// <summary>Get overall cost summary across all projects.</summary>
    Task<CostSummaryDto> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>Get cost breakdown per project.</summary>
    Task<IReadOnlyList<ProjectCostDto>> GetCostsByProjectAsync(CancellationToken ct = default);

    /// <summary>Get per-task cost breakdown for a specific project.</summary>
    Task<IReadOnlyList<TaskCostDto>> GetCostsByProjectIdAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Get per-task cost breakdown for a specific session.</summary>
    Task<IReadOnlyList<TaskCostDto>> GetCostsBySessionIdAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Estimate cost for a planned Ralph run.</summary>
    Task<CostEstimateDto> EstimateCostAsync(string modelId, int maxIterations, int estimatedPromptTokens, CancellationToken ct = default);

    /// <summary>Get all configured model pricing.</summary>
    Task<IReadOnlyList<ModelPricingDto>> GetPricingAsync(CancellationToken ct = default);
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Daedalus.Application/Daedalus.Application.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Daedalus.Application/Abstractions/ICostAnalyticsService.cs
git commit -m "feat: add ICostAnalyticsService interface"
```

---

## Task 10: Implement CostAnalyticsService

**Files:**
- Create: `src/Daedalus.Infrastructure/Services/CostAnalyticsService.cs`

**Step 1: Create the service implementation**

Create `src/Daedalus.Infrastructure/Services/CostAnalyticsService.cs`:

```csharp
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Provides cost analytics by querying TaskExecution data and applying model pricing.
/// </summary>
public sealed class CostAnalyticsService(
    ApplicationDbContext dbContext,
    IOptions<ModelPricingConfiguration> pricingOptions) : ICostAnalyticsService
{
    private readonly ModelPricingConfiguration _pricing = pricingOptions.Value;

    public async Task<CostSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var stats = await dbContext.TaskExecutions
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalInputTokens = g.Sum(e => (long)e.InputTokens),
                TotalOutputTokens = g.Sum(e => (long)e.OutputTokens),
                TotalExecutions = g.Count(),
                TotalTasks = g.Select(e => e.TaskId).Distinct().Count()
            })
            .FirstOrDefaultAsync(ct);

        if (stats is null)
        {
            return new CostSummaryDto(0, 0, 0m, 0, 0);
        }

        var totalCost = CalculateCostFromExecutions(
            stats.TotalInputTokens, stats.TotalOutputTokens);

        return new CostSummaryDto(
            stats.TotalInputTokens,
            stats.TotalOutputTokens,
            totalCost,
            stats.TotalExecutions,
            stats.TotalTasks);
    }

    public async Task<IReadOnlyList<ProjectCostDto>> GetCostsByProjectAsync(CancellationToken ct = default)
    {
        var projectCosts = await dbContext.TaskExecutions
            .Join(dbContext.Tasks,
                e => e.TaskId,
                t => t.Id,
                (e, t) => new { Execution = e, Task = t })
            .Join(dbContext.Projects,
                et => et.Task.ProjectId,
                p => p.Id,
                (et, p) => new { et.Execution, et.Task, Project = p })
            .GroupBy(x => new { x.Project.Id, x.Project.ProjectName })
            .Select(g => new ProjectCostDto(
                g.Key.Id,
                g.Key.ProjectName,
                g.Sum(x => (long)x.Execution.InputTokens),
                g.Sum(x => (long)x.Execution.OutputTokens),
                0m, // calculated client-side
                g.Count()))
            .ToListAsync(ct);

        // Calculate costs using pricing config (can't do in SQL)
        return projectCosts
            .Select(p => p with { EstimatedCost = CalculateCostFromExecutions(p.InputTokens, p.OutputTokens) })
            .ToList();
    }

    public async Task<IReadOnlyList<TaskCostDto>> GetCostsByProjectIdAsync(Guid projectId, CancellationToken ct = default)
    {
        var taskCosts = await dbContext.TaskExecutions
            .Join(dbContext.Tasks.Where(t => t.ProjectId == projectId),
                e => e.TaskId,
                t => t.Id,
                (e, t) => new { Execution = e, Task = t })
            .GroupBy(x => new { x.Task.Id, x.Task.Title })
            .Select(g => new TaskCostDto(
                g.Key.Id,
                g.Key.Title,
                g.Sum(x => (long)x.Execution.InputTokens),
                g.Sum(x => (long)x.Execution.OutputTokens),
                0m,
                g.Count()))
            .ToListAsync(ct);

        return taskCosts
            .Select(t => t with { EstimatedCost = CalculateCostFromExecutions(t.InputTokens, t.OutputTokens) })
            .ToList();
    }

    public async Task<IReadOnlyList<TaskCostDto>> GetCostsBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        var taskCosts = await dbContext.TaskExecutions
            .Where(e => e.SessionId == sessionId)
            .Join(dbContext.Tasks,
                e => e.TaskId,
                t => t.Id,
                (e, t) => new { Execution = e, Task = t })
            .GroupBy(x => new { x.Task.Id, x.Task.Title })
            .Select(g => new TaskCostDto(
                g.Key.Id,
                g.Key.Title,
                g.Sum(x => (long)x.Execution.InputTokens),
                g.Sum(x => (long)x.Execution.OutputTokens),
                0m,
                g.Count()))
            .ToListAsync(ct);

        return taskCosts
            .Select(t => t with { EstimatedCost = CalculateCostFromExecutions(t.InputTokens, t.OutputTokens) })
            .ToList();
    }

    public async Task<CostEstimateDto> EstimateCostAsync(
        string modelId, int maxIterations, int estimatedPromptTokens, CancellationToken ct = default)
    {
        // Get average output tokens from historical data
        var avgOutputTokens = await dbContext.TaskExecutions
            .Where(e => e.OutputTokens > 0)
            .AverageAsync(e => (double?)e.OutputTokens, ct) ?? 4000.0;

        var estimatedResponseTokens = (int)avgOutputTokens;

        if (!_pricing.Models.TryGetValue(modelId, out var pricing))
        {
            // Fallback to first configured model
            var first = _pricing.Models.FirstOrDefault();
            modelId = first.Key ?? modelId;
            pricing = first.Value ?? new ModelPricing
            {
                DisplayName = modelId,
                InputTokenPricePerMillion = 3.0m,
                OutputTokenPricePerMillion = 15.0m
            };
        }

        var inputCostPerIteration = estimatedPromptTokens * pricing.InputTokenPricePerMillion / 1_000_000m;
        var outputCostPerIteration = estimatedResponseTokens * pricing.OutputTokenPricePerMillion / 1_000_000m;
        var costPerIteration = inputCostPerIteration + outputCostPerIteration;

        // Min estimate: completes in ~30% of max iterations
        // Max estimate: uses all max iterations
        var minIterations = Math.Max(1, (int)(maxIterations * 0.3));
        var estimatedMinCost = Math.Round(costPerIteration * minIterations, 4);
        var estimatedMaxCost = Math.Round(costPerIteration * maxIterations, 4);

        return new CostEstimateDto(
            modelId,
            pricing.DisplayName,
            maxIterations,
            estimatedPromptTokens,
            estimatedResponseTokens,
            estimatedMinCost,
            estimatedMaxCost);
    }

    public Task<IReadOnlyList<ModelPricingDto>> GetPricingAsync(CancellationToken ct = default)
    {
        var result = _pricing.Models
            .Select(kvp => new ModelPricingDto(
                kvp.Key,
                kvp.Value.DisplayName,
                kvp.Value.InputTokenPricePerMillion,
                kvp.Value.OutputTokenPricePerMillion))
            .ToList() as IReadOnlyList<ModelPricingDto>;

        return Task.FromResult(result);
    }

    /// <summary>
    ///     Calculates cost using a blended rate across all configured models.
    ///     For per-model accuracy, we'd need to group by ModelId, but this gives a
    ///     reasonable estimate when most executions use the same model.
    /// </summary>
    private decimal CalculateCostFromExecutions(long inputTokens, long outputTokens)
    {
        // Use the first configured model as the default pricing
        var pricing = _pricing.Models.Values.FirstOrDefault();
        if (pricing is null)
        {
            return 0m;
        }

        var inputCost = inputTokens * pricing.InputTokenPricePerMillion / 1_000_000m;
        var outputCost = outputTokens * pricing.OutputTokenPricePerMillion / 1_000_000m;
        return Math.Round(inputCost + outputCost, 4);
    }
}
```

**Step 2: Register the service in Program.cs**

In `src/Daedalus.Api/Program.cs`, add after the query service registrations (after line 61):

```csharp
builder.Services.AddScoped<ICostAnalyticsService, CostAnalyticsService>();
```

Add to the usings at the top:
```csharp
using Daedalus.Infrastructure.Services;
```
(Check if this using already exists — it may from other services.)

**Step 3: Build to verify**

Run: `dotnet build`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Daedalus.Infrastructure/Services/CostAnalyticsService.cs src/Daedalus.Api/Program.cs
git commit -m "feat: implement CostAnalyticsService with aggregate queries"
```

---

## Task 11: Create CostAnalyticsController

**Files:**
- Create: `src/Daedalus.Api/Controllers/CostAnalyticsController.cs`

**Step 1: Create the controller**

Create `src/Daedalus.Api/Controllers/CostAnalyticsController.cs`:

```csharp
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Controllers;

/// <summary>API endpoints for cost analytics and estimation.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/cost-analytics")]
[Authorize]
[Produces("application/json")]
public sealed partial class CostAnalyticsController(
    ICostAnalyticsService costService,
    ILogger<CostAnalyticsController> logger) : ControllerBase
{
    [LoggerMessage(EventId = 200, Level = LogLevel.Error, Message = "Error retrieving cost analytics")]
    private static partial void LogErrorRetrievingCosts(ILogger logger, Exception ex);

    /// <summary>Get overall cost summary.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("summary")]
    [ProducesResponseType(typeof(CostSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken ct = default)
    {
        try
        {
            var result = await costService.GetSummaryAsync(ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get cost breakdown by project.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("by-project")]
    [ProducesResponseType(typeof(IReadOnlyList<ProjectCostDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByProject(CancellationToken ct = default)
    {
        try
        {
            var result = await costService.GetCostsByProjectAsync(ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get per-task cost breakdown for a project.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("by-project/{id:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<TaskCostDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByProjectId(Guid id, CancellationToken ct = default)
    {
        try
        {
            var result = await costService.GetCostsByProjectIdAsync(id, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get per-task cost breakdown for a session.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("by-session/{id:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<TaskCostDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBySessionId(Guid id, CancellationToken ct = default)
    {
        try
        {
            var result = await costService.GetCostsBySessionIdAsync(id, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Estimate cost for a planned Ralph run.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("estimate")]
    [ProducesResponseType(typeof(CostEstimateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> EstimateCost(
        [FromQuery] string modelId,
        [FromQuery] int maxIterations = 10,
        [FromQuery] int estimatedPromptTokens = 4000,
        CancellationToken ct = default)
    {
        try
        {
            var result = await costService.EstimateCostAsync(modelId, maxIterations, estimatedPromptTokens, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get configured model pricing.</summary>
    [Authorize(Policy = "TaskRead")]
    [HttpGet("pricing")]
    [ProducesResponseType(typeof(IReadOnlyList<ModelPricingDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPricing(CancellationToken ct = default)
    {
        try
        {
            var result = await costService.GetPricingAsync(ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingCosts(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Daedalus.Api/Daedalus.Api.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Daedalus.Api/Controllers/CostAnalyticsController.cs
git commit -m "feat: add CostAnalyticsController with all cost endpoints"
```

---

## Task 12: Add Cost Analytics API Methods to Frontend ApiClient

**Files:**
- Modify: `src/Daedalus.Web/Services/ApiClient.cs`

**Step 1: Add DTOs to the Web project**

The Web project needs the cost analytics DTOs. Check if DTOs are shared or duplicated. Looking at the existing code, the Web project has its own DTOs at the top of `ApiClient.cs` or in separate files. Search for where DTOs live in the Web project.

If DTOs are duplicated in the Web project, add the cost DTOs there. If they're shared via a project reference, they should already be available.

**Step 2: Add cost analytics methods to ApiClient**

Add after the Ralph Config section (after line 200) in `ApiClient.cs`:

```csharp
// Cost Analytics
public async Task<Result<CostSummaryDto>> GetCostSummaryAsync(CancellationToken ct = default) =>
    await GetAsync<CostSummaryDto>("/api/cost-analytics/summary", ct);

public async Task<Result<List<ProjectCostDto>>> GetCostsByProjectAsync(CancellationToken ct = default) =>
    await GetAsync<List<ProjectCostDto>>("/api/cost-analytics/by-project", ct);

public async Task<Result<List<TaskCostDto>>> GetCostsByProjectIdAsync(Guid projectId, CancellationToken ct = default) =>
    await GetAsync<List<TaskCostDto>>($"/api/cost-analytics/by-project/{projectId}", ct);

public async Task<Result<List<TaskCostDto>>> GetCostsBySessionIdAsync(Guid sessionId, CancellationToken ct = default) =>
    await GetAsync<List<TaskCostDto>>($"/api/cost-analytics/by-session/{sessionId}", ct);

public async Task<Result<CostEstimateDto>> EstimateCostAsync(
    string modelId, int maxIterations = 10, int estimatedPromptTokens = 4000,
    CancellationToken ct = default) =>
    await GetAsync<CostEstimateDto>(
        $"/api/cost-analytics/estimate?modelId={Uri.EscapeDataString(modelId)}&maxIterations={maxIterations}&estimatedPromptTokens={estimatedPromptTokens}",
        ct);

public async Task<Result<List<ModelPricingDto>>> GetModelPricingAsync(CancellationToken ct = default) =>
    await GetAsync<List<ModelPricingDto>>("/api/cost-analytics/pricing", ct);
```

**Step 3: Add DTO records to the Web project**

If DTOs are not shared, add the following records to wherever the Web project keeps its DTO copies (check for a `Dtos.cs` file or similar in the Web project, or add at the top of `ApiClient.cs` where other DTOs are defined):

```csharp
public record CostSummaryDto(long TotalInputTokens, long TotalOutputTokens, decimal TotalCost, int TotalExecutions, int TotalTasks);
public record ProjectCostDto(Guid ProjectId, string ProjectName, long InputTokens, long OutputTokens, decimal EstimatedCost, int ExecutionCount);
public record TaskCostDto(Guid TaskId, string TaskTitle, long InputTokens, long OutputTokens, decimal EstimatedCost, int IterationCount);
public record CostEstimateDto(string ModelId, string ModelDisplayName, int MaxIterations, int EstimatedPromptTokens, int EstimatedResponseTokens, decimal EstimatedMinCost, decimal EstimatedMaxCost);
public record ModelPricingDto(string ModelId, string DisplayName, decimal InputPricePerMillion, decimal OutputPricePerMillion);
```

**Step 4: Build to verify**

Run: `dotnet build src/Daedalus.Web/Daedalus.Web.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Daedalus.Web/Services/ApiClient.cs
git commit -m "feat: add cost analytics API client methods"
```

---

## Task 13: Create the Costs Page (Blazor)

**Files:**
- Create: `src/Daedalus.Web/Pages/Costs.razor`
- Modify: `src/Daedalus.Web/Components/MainLayout.razor`

**Step 1: Add navigation link to MainLayout**

In `src/Daedalus.Web/Components/MainLayout.razor`, add after the Executions nav link (after line 29):

```razor
<RadzenPanelMenuItem Text="Costs" Icon="attach_money" Path="costs"/>
```

**Step 2: Create the Costs page**

Create `src/Daedalus.Web/Pages/Costs.razor`:

```razor
@page "/costs"
@inject ApiClient Api
@implements IAsyncDisposable

<RadzenStack Gap="0.25rem" class="rz-mb-4">
    <RadzenText TextStyle="TextStyle.H4" Style="margin: 0;">Cost Analytics</RadzenText>
    <RadzenText TextStyle="TextStyle.Body2" Style="opacity: 0.6; margin: 0;">
        Token usage and cost tracking across projects
    </RadzenText>
</RadzenStack>

@if (!string.IsNullOrEmpty(_errorMessage))
{
    <RadzenAlert AlertStyle="AlertStyle.Danger" Shade="Shade.Lighter" AllowClose="true" class="rz-mb-4">
        @_errorMessage
    </RadzenAlert>
}

@* Summary Cards *@
<RadzenRow Gap="1rem" class="rz-mb-6" data-testid="cost-stats-row">
    <RadzenColumn Size="12" SizeMD="3">
        <RadzenCard data-testid="stat-total-tokens">
            <RadzenStack Orientation="Orientation.Horizontal" AlignItems="AlignItems.Center" Gap="1rem">
                <div class="stat-icon" style="background: linear-gradient(135deg, var(--rz-primary), var(--rz-primary-dark));">
                    <RadzenIcon Icon="token"/>
                </div>
                <RadzenStack Gap="0">
                    <RadzenText TextStyle="TextStyle.H3" Style="margin: 0;">@FormatNumber(_summary?.TotalInputTokens + _summary?.TotalOutputTokens ?? 0)</RadzenText>
                    <RadzenText TextStyle="TextStyle.Caption" Style="margin: 0; opacity: 0.6;">Total Tokens</RadzenText>
                </RadzenStack>
            </RadzenStack>
        </RadzenCard>
    </RadzenColumn>
    <RadzenColumn Size="12" SizeMD="3">
        <RadzenCard data-testid="stat-total-cost">
            <RadzenStack Orientation="Orientation.Horizontal" AlignItems="AlignItems.Center" Gap="1rem">
                <div class="stat-icon" style="background: linear-gradient(135deg, var(--rz-success), var(--rz-success-dark));">
                    <RadzenIcon Icon="attach_money"/>
                </div>
                <RadzenStack Gap="0">
                    <RadzenText TextStyle="TextStyle.H3" Style="margin: 0;">@FormatCurrency(_summary?.TotalCost ?? 0)</RadzenText>
                    <RadzenText TextStyle="TextStyle.Caption" Style="margin: 0; opacity: 0.6;">Total Cost</RadzenText>
                </RadzenStack>
            </RadzenStack>
        </RadzenCard>
    </RadzenColumn>
    <RadzenColumn Size="12" SizeMD="3">
        <RadzenCard data-testid="stat-avg-cost">
            <RadzenStack Orientation="Orientation.Horizontal" AlignItems="AlignItems.Center" Gap="1rem">
                <div class="stat-icon" style="background: linear-gradient(135deg, var(--rz-info), var(--rz-info-dark));">
                    <RadzenIcon Icon="analytics"/>
                </div>
                <RadzenStack Gap="0">
                    <RadzenText TextStyle="TextStyle.H3" Style="margin: 0;">@FormatCurrency(_summary is { TotalTasks: > 0 } ? _summary.TotalCost / _summary.TotalTasks : 0)</RadzenText>
                    <RadzenText TextStyle="TextStyle.Caption" Style="margin: 0; opacity: 0.6;">Avg Cost / Task</RadzenText>
                </RadzenStack>
            </RadzenStack>
        </RadzenCard>
    </RadzenColumn>
    <RadzenColumn Size="12" SizeMD="3">
        <RadzenCard data-testid="stat-total-executions">
            <RadzenStack Orientation="Orientation.Horizontal" AlignItems="AlignItems.Center" Gap="1rem">
                <div class="stat-icon" style="background: linear-gradient(135deg, var(--rz-warning), var(--rz-warning-dark));">
                    <RadzenIcon Icon="speed"/>
                </div>
                <RadzenStack Gap="0">
                    <RadzenText TextStyle="TextStyle.H3" Style="margin: 0;">@FormatNumber(_summary?.TotalExecutions ?? 0)</RadzenText>
                    <RadzenText TextStyle="TextStyle.Caption" Style="margin: 0; opacity: 0.6;">Total Executions</RadzenText>
                </RadzenStack>
            </RadzenStack>
        </RadzenCard>
    </RadzenColumn>
</RadzenRow>

<RadzenRow Gap="1rem" class="rz-mb-6">
    @* Cost Estimator *@
    <RadzenColumn Size="12" SizeMD="5">
        <RadzenCard data-testid="cost-estimator">
            <RadzenText TextStyle="TextStyle.H6" class="rz-mb-4">
                <RadzenIcon Icon="calculate"/>
                Cost Estimator
            </RadzenText>
            <RadzenStack Gap="0.75rem">
                <div>
                    <RadzenLabel Text="Model" Component="ModelSelect"/>
                    <RadzenDropDown @bind-Value="_selectedModelId" Data="_pricingModels"
                                   TextProperty="DisplayName" ValueProperty="ModelId"
                                   Style="width: 100%;" Name="ModelSelect"
                                   Change="@OnEstimateParamsChanged"/>
                </div>
                <div>
                    <RadzenLabel Text="Max Iterations" Component="MaxIterations"/>
                    <RadzenNumeric @bind-Value="_estimateMaxIterations" Min="1" Max="100"
                                  Style="width: 100%;" Name="MaxIterations"
                                  Change="@(_ => OnEstimateParamsChanged())"/>
                </div>
                <div>
                    <RadzenLabel Text="Estimated Prompt Tokens" Component="PromptTokens"/>
                    <RadzenNumeric @bind-Value="_estimatePromptTokens" Min="100" Max="100000"
                                  Style="width: 100%;" Name="PromptTokens"
                                  Change="@(_ => OnEstimateParamsChanged())"/>
                </div>

                @if (_estimate is not null)
                {
                    <RadzenCard Style="background: var(--rz-base-200);">
                        <RadzenText TextStyle="TextStyle.Subtitle2" Style="margin: 0;">Estimated Cost Range</RadzenText>
                        <RadzenText TextStyle="TextStyle.H4" Style="margin: 0.25rem 0 0 0; color: var(--rz-success);">
                            @FormatCurrency(_estimate.EstimatedMinCost) — @FormatCurrency(_estimate.EstimatedMaxCost)
                        </RadzenText>
                        <RadzenText TextStyle="TextStyle.Caption" Style="opacity: 0.6; margin: 0.25rem 0 0 0;">
                            Based on @_estimate.EstimatedResponseTokens avg output tokens/iteration
                        </RadzenText>
                    </RadzenCard>
                }
            </RadzenStack>
        </RadzenCard>
    </RadzenColumn>

    @* Model Pricing Reference *@
    <RadzenColumn Size="12" SizeMD="7">
        <RadzenCard data-testid="model-pricing">
            <RadzenText TextStyle="TextStyle.H6" class="rz-mb-4">
                <RadzenIcon Icon="price_check"/>
                Model Pricing
            </RadzenText>
            <RadzenDataGrid Data="_pricingModels" TItem="ModelPricingDto" Density="Density.Compact">
                <Columns>
                    <RadzenDataGridColumn TItem="ModelPricingDto" Property="DisplayName" Title="Model"/>
                    <RadzenDataGridColumn TItem="ModelPricingDto" Property="InputPricePerMillion" Title="Input $/1M" FormatString="{0:C2}"/>
                    <RadzenDataGridColumn TItem="ModelPricingDto" Property="OutputPricePerMillion" Title="Output $/1M" FormatString="{0:C2}"/>
                </Columns>
            </RadzenDataGrid>
        </RadzenCard>
    </RadzenColumn>
</RadzenRow>

@* Per-Project Cost Table *@
<RadzenCard data-testid="project-costs">
    <RadzenText TextStyle="TextStyle.H6" class="rz-mb-4">
        <RadzenIcon Icon="folder_special"/>
        Cost by Project
    </RadzenText>
    <RadzenDataGrid Data="_projectCosts" TItem="ProjectCostDto" AllowSorting="true"
                    RowExpand="@OnRowExpand" ExpandMode="DataGridExpandMode.Single">
        <Template Context="project">
            @* Expanded row: per-task breakdown *@
            <RadzenDataGrid Data="@GetTaskCosts(project.ProjectId)" TItem="TaskCostDto"
                            Density="Density.Compact" Style="margin: 0.5rem 1rem;">
                <Columns>
                    <RadzenDataGridColumn TItem="TaskCostDto" Property="TaskTitle" Title="Task"/>
                    <RadzenDataGridColumn TItem="TaskCostDto" Property="InputTokens" Title="Input Tokens" FormatString="{0:N0}" Width="120px"/>
                    <RadzenDataGridColumn TItem="TaskCostDto" Property="OutputTokens" Title="Output Tokens" FormatString="{0:N0}" Width="120px"/>
                    <RadzenDataGridColumn TItem="TaskCostDto" Property="EstimatedCost" Title="Cost" FormatString="{0:C4}" Width="100px"/>
                    <RadzenDataGridColumn TItem="TaskCostDto" Property="IterationCount" Title="Iterations" Width="100px"/>
                </Columns>
            </RadzenDataGrid>
        </Template>
        <Columns>
            <RadzenDataGridColumn TItem="ProjectCostDto" Property="ProjectName" Title="Project"/>
            <RadzenDataGridColumn TItem="ProjectCostDto" Property="InputTokens" Title="Input Tokens" FormatString="{0:N0}" Width="130px"/>
            <RadzenDataGridColumn TItem="ProjectCostDto" Property="OutputTokens" Title="Output Tokens" FormatString="{0:N0}" Width="130px"/>
            <RadzenDataGridColumn TItem="ProjectCostDto" Property="EstimatedCost" Title="Total Cost" FormatString="{0:C4}" Width="120px"/>
            <RadzenDataGridColumn TItem="ProjectCostDto" Property="ExecutionCount" Title="Executions" Width="110px"/>
            <RadzenDataGridColumn TItem="ProjectCostDto" Title="Avg/Execution" Width="120px">
                <Template Context="p">
                    @(p.ExecutionCount > 0 ? FormatCurrency(p.EstimatedCost / p.ExecutionCount) : "$0.00")
                </Template>
            </RadzenDataGridColumn>
        </Columns>
    </RadzenDataGrid>
</RadzenCard>

@code {
    private CostSummaryDto? _summary;
    private List<ProjectCostDto> _projectCosts = [];
    private List<ModelPricingDto> _pricingModels = [];
    private CostEstimateDto? _estimate;
    private string _errorMessage = "";
    private CancellationTokenSource? _cts;

    // Estimator inputs
    private string _selectedModelId = "claude-sonnet-4-20250514";
    private int _estimateMaxIterations = 10;
    private int _estimatePromptTokens = 4000;

    // Expanded row task costs cache
    private readonly Dictionary<Guid, List<TaskCostDto>> _taskCostsCache = new();

    protected override async Task OnInitializedAsync()
    {
        _cts = new CancellationTokenSource();
        await LoadDataAsync(_cts.Token);
    }

    private async Task LoadDataAsync(CancellationToken ct)
    {
        try
        {
            var summaryTask = Api.GetCostSummaryAsync(ct);
            var projectsTask = Api.GetCostsByProjectAsync(ct);
            var pricingTask = Api.GetModelPricingAsync(ct);

            await Task.WhenAll(summaryTask, projectsTask, pricingTask);

            _summary = summaryTask.Result.Match(s => s, _ => null);
            _projectCosts = projectsTask.Result.Match(p => p, _ => []);
            _pricingModels = pricingTask.Result.Match(p => p, _ => []);

            if (_pricingModels.Count > 0)
            {
                _selectedModelId = _pricingModels[0].ModelId;
            }

            await LoadEstimateAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Silently ignore cancellation
        }
        catch (Exception ex)
        {
            _errorMessage = $"Error loading cost data: {ex.Message}";
        }
    }

    private async Task LoadEstimateAsync(CancellationToken ct)
    {
        var estimateResult = await Api.EstimateCostAsync(
            _selectedModelId, _estimateMaxIterations, _estimatePromptTokens, ct);
        _estimate = estimateResult.Match(e => e, _ => null);
    }

    private async void OnEstimateParamsChanged()
    {
        if (_cts is null) return;
        await LoadEstimateAsync(_cts.Token);
        StateHasChanged();
    }

    private async void OnRowExpand(ProjectCostDto project)
    {
        if (_taskCostsCache.ContainsKey(project.ProjectId)) return;
        if (_cts is null) return;

        var result = await Api.GetCostsByProjectIdAsync(project.ProjectId, _cts.Token);
        result.Match(
            tasks => _taskCostsCache[project.ProjectId] = tasks,
            _ => { });
        StateHasChanged();
    }

    private List<TaskCostDto> GetTaskCosts(Guid projectId)
    {
        return _taskCostsCache.TryGetValue(projectId, out var tasks) ? tasks : [];
    }

    private static string FormatNumber(long number) => number switch
    {
        >= 1_000_000 => $"{number / 1_000_000.0:F1}M",
        >= 1_000 => $"{number / 1_000.0:F1}K",
        _ => number.ToString("N0")
    };

    private static string FormatCurrency(decimal amount) => amount.ToString("C2");

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
    }
}
```

**Step 3: Build to verify**

Run: `dotnet build src/Daedalus.Web/Daedalus.Web.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Daedalus.Web/Pages/Costs.razor src/Daedalus.Web/Components/MainLayout.razor
git commit -m "feat: add Costs dashboard page with estimator and per-project breakdown"
```

---

## Task 14: Fix Remaining Build Errors and Run Full Build

**Files:**
- Various — fix any compilation errors from the interface change

**Step 1: Find all consumers of the old InvokeAsync signature**

The change from `Result<string>` to `Result<LlmInvocationResult>` may break other callers. Search for them:

Run: `grep -rn "InvokeAsync" src/ --include="*.cs" | grep -v "Subagent" | grep -v ".Designer." | grep -v "obj/"`

Fix each caller to use `.Value.Response` instead of `.Value` where they extract the string.

**Step 2: Fix test mocks**

Any test that mocks `IRalphAgentFactory.InvokeAsync` will need to return `Result.Success(new LlmInvocationResult { Response = "..." })` instead of `Result.Success("...")`.

Search: `grep -rn "InvokeAsync" tests/ --include="*.cs" | grep -v "Subagent"`

Update each mock setup accordingly.

**Step 3: Fix DTO mapping for TaskExecutionDto**

Find where `TaskExecutionDto` is constructed and add the three new fields. Search:

Run: `grep -rn "TaskExecutionDto" src/ --include="*.cs" | grep -v "obj/"`

**Step 4: Full build**

Run: `dotnet build`
Expected: 0 errors

**Step 5: Run existing tests**

Run: `dotnet test --no-build`
Expected: All existing tests pass (some may need mock updates from step 2)

**Step 6: Commit all fixes**

```bash
git add -A
git commit -m "fix: update all callers for LlmInvocationResult and new DTO fields"
```

---

## Task 15: Final Verification

**Step 1: Clean build**

Run: `dotnet clean && dotnet build`
Expected: 0 errors, 0 warnings (or only pre-existing warnings)

**Step 2: Run all tests**

Run: `dotnet test`
Expected: All tests pass

**Step 3: Verify migration applies**

Run: `dotnet ef database update --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Migrations` (or start with Aspire)
Expected: Migration applies cleanly

**Step 4: Final commit**

```bash
git add -A
git commit -m "feat: costs dashboard complete — token tracking, analytics, and estimator"
```
