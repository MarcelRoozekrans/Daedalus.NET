# Costs Dashboard Design

**Date**: 2026-03-01
**Status**: Approved

## Overview

Add a dedicated `/costs` page to Daedalus that tracks actual token usage per LLM invocation, calculates costs using configurable model pricing, and provides a cost estimator for planning Ralph runs.

## Requirements

1. **Actual token tracking** — persist InputTokens, OutputTokens, and ModelId from every LLM call
2. **Configurable pricing** — model pricing stored in `appsettings.json`, easy to update
3. **Both detailed and aggregated views** — project-level totals with drill-down to per-task/execution detail
4. **Cost estimator** — estimate cost before starting a Ralph run based on MaxIterations and prompt size

## Data Model Changes

### TaskExecution (new columns)

| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `InputTokens` | `int` | `0` | Tokens in the prompt |
| `OutputTokens` | `int` | `0` | Tokens in the response |
| `ModelId` | `string?` | `null` | Model used (e.g., `claude-sonnet-4-20250514`) |

### AnalysisIteration (new columns)

Same three columns as TaskExecution — InputTokens, OutputTokens, ModelId.

### ModelPricingConfiguration (new config class)

```csharp
public class ModelPricingConfiguration
{
    public Dictionary<string, ModelPricing> Models { get; set; } = new();
}

public class ModelPricing
{
    public string DisplayName { get; set; }
    public decimal InputTokenPricePerMillion { get; set; }
    public decimal OutputTokenPricePerMillion { get; set; }
}
```

### appsettings.json

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
    }
  }
}
```

## Pipeline Changes (Token Persistence)

### TaskExecution path

In `RalphLoopPipelineService` (or wherever `TaskExecution` records are created), pass token counts from the LLM response into the entity.

### AnalysisIteration path

In `RalphLoopOrchestrator`, pass token counts from `SubagentResult` into `AnalysisIteration.Create()`.

### RalphAgentFactory

The `InvokeAsync` method (primary loop) currently returns `Result<string>`. It needs to return token usage alongside the response text — either via a richer return type or an out parameter.

## API Layer

### New Controller: CostAnalyticsController (`/api/cost-analytics`)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/summary` | Overall totals: total input/output tokens, total cost, execution count |
| `GET` | `/by-project` | Per-project breakdown |
| `GET` | `/by-project/{id}` | Single project with per-task token breakdown |
| `GET` | `/by-session/{id}` | Per-session cost breakdown |
| `GET` | `/estimate` | Estimated cost for a run (query: maxIterations, estimatedPromptTokens) |
| `GET` | `/pricing` | Returns configured model pricing table |

### New DTOs

```csharp
record CostSummaryDto(
    long TotalInputTokens, long TotalOutputTokens,
    decimal TotalCost, int TotalExecutions, int TotalTasks);

record ProjectCostDto(
    Guid ProjectId, string ProjectName,
    long InputTokens, long OutputTokens,
    decimal EstimatedCost, int ExecutionCount);

record TaskCostDto(
    Guid TaskId, string TaskTitle,
    long InputTokens, long OutputTokens,
    decimal EstimatedCost, int IterationCount);

record CostEstimateDto(
    string ModelId, int MaxIterations,
    int EstimatedPromptTokens, int EstimatedResponseTokens,
    decimal EstimatedMinCost, decimal EstimatedMaxCost);

record ModelPricingDto(
    string ModelId, string DisplayName,
    decimal InputPricePerMillion, decimal OutputPricePerMillion);
```

### New Service: ICostAnalyticsService

Handles aggregate queries against the database and cost calculations using the pricing configuration.

## UI Design — `/costs` Page

### Layout (top to bottom)

**1. Summary Cards Row** (4 cards, same style as Home dashboard):
- Total Tokens (input + output combined)
- Total Cost (dollar amount)
- Avg Cost/Task
- Total Executions

**2. Cost Estimator Panel** (RadzenCard):
- Model selector dropdown (populated from pricing config)
- Max Iterations input (number)
- Estimated Prompt Size input (tokens)
- Output: shows estimated cost range (min-max)
- Note: uses average historical output tokens to project costs

**3. Per-Project Cost Table** (RadzenDataGrid):
- Columns: Project Name, Input Tokens, Output Tokens, Total Cost, Executions, Avg Cost/Execution
- Sortable columns
- Click row to expand showing per-task breakdown

### Sidebar Navigation

Add "Costs" link with `attach_money` icon between "Executions" and "Ralph Config".

## EF Core Migration

A new migration adding InputTokens (int, default 0), OutputTokens (int, default 0), and ModelId (string?, nullable) to both TaskExecutions and AnalysisIterations tables.

## Files to Create/Modify

### New files
- `src/Daedalus.Application/Configuration/ModelPricingConfiguration.cs`
- `src/Daedalus.Application/DTOs/CostAnalyticsDtos.cs`
- `src/Daedalus.Application/Abstractions/ICostAnalyticsService.cs`
- `src/Daedalus.Infrastructure/Services/CostAnalyticsService.cs`
- `src/Daedalus.Api/Controllers/CostAnalyticsController.cs`
- `src/Daedalus.Web/Pages/Costs.razor`
- `src/Daedalus.Web/Pages/Costs.razor.css` (if needed)
- EF Core migration file

### Modified files
- `src/Daedalus.Domain/Entities/TaskExecution.cs` — add token/model fields
- `src/Daedalus.Domain/CodeAnalysis/AnalysisIteration.cs` — add token/model fields
- `src/Daedalus.Application/DTOs/TaskExecutionDto.cs` — add token/model fields
- `src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs` — configure new columns
- `src/Daedalus.Infrastructure/Agents/RalphAgentFactory.cs` — return token data from InvokeAsync
- `src/Daedalus.Application/Services/RalphLoopPipelineService.cs` — persist tokens on TaskExecution
- `src/Daedalus.Infrastructure/Services/CodeAnalysis/RalphLoopOrchestrator.cs` — persist tokens on AnalysisIteration
- `src/Daedalus.Web/Components/MainLayout.razor` — add Costs nav link
- `src/Daedalus.Web/Services/ApiClient.cs` — add cost analytics API methods
- `src/Daedalus.Api/appsettings.json` — add ModelPricing section
- `src/Daedalus.Api/Program.cs` — register ICostAnalyticsService and ModelPricingConfiguration
