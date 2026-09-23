using Daedalus.Application.Configuration;
using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Complexity = Daedalus.Domain.Entities.Complexity;
using DomainProject = Daedalus.Domain.Entities.Project;
using DomainTask = Daedalus.Domain.Entities.Task;
using Priority = Daedalus.Domain.Entities.Priority;
using TaskExecution = Daedalus.Domain.Entities.TaskExecution;

namespace Daedalus.Tests.Unit.Infrastructure.Services;

/// <summary>
///     Unit tests for <see cref="CostAnalyticsService"/> — the guard against silently relabelling an unpriced or
///     unattributed model's tokens under another model's name and rate, on every method that computes a cost
///     (<see cref="CostAnalyticsService.EstimateCostAsync"/>, <see cref="CostAnalyticsService.GetSummaryAsync"/>,
///     <see cref="CostAnalyticsService.GetCostsByProjectAsync"/>, <see cref="CostAnalyticsService.GetCostsByProjectIdAsync"/>,
///     and <see cref="CostAnalyticsService.GetCostsBySessionIdAsync"/>).
/// </summary>
public sealed class CostAnalyticsServiceTests : IAsyncDisposable
{
    private const string PricedModelId = "claude-sonnet-4-20250514";
    private const string ModelA = "model-a";
    private const string ModelB = "model-b";
    private readonly ApplicationDbContext _dbContext;

    public CostAnalyticsServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    private static IOptions<ModelPricingConfiguration> PricingWithOneModel() =>
        Options.Create(new ModelPricingConfiguration
        {
            Models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
            {
                [PricedModelId] = new()
                {
                    DisplayName = "Claude Sonnet 4",
                    InputTokenPricePerMillion = 3.0m,
                    OutputTokenPricePerMillion = 15.0m
                }
            }
        });

    /// <summary>
    ///     Model-a and model-b are priced far enough apart (3x on both input and output) that blending their token
    ///     counts before pricing — the original defect — produces a visibly different total than pricing each
    ///     group separately and summing. That gap is what each grouping test below asserts on.
    /// </summary>
    private static IOptions<ModelPricingConfiguration> PricingWithTwoModels() =>
        Options.Create(new ModelPricingConfiguration
        {
            Models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
            {
                [ModelA] = new() { DisplayName = "Model A", InputTokenPricePerMillion = 3.0m, OutputTokenPricePerMillion = 15.0m },
                [ModelB] = new() { DisplayName = "Model B", InputTokenPricePerMillion = 1.0m, OutputTokenPricePerMillion = 5.0m }
            }
        });

    /// <summary>Adds a Project and a single Task within it, and returns their ids. Does not call SaveChanges.</summary>
    private (Guid ProjectId, Guid TaskId) SeedProjectWithTask(string taskLabel = "TASK-1")
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var project = DomainProject.Create(projectId, "Test Project", "Test Description").Value;
        var task = DomainTask.Create(
            taskId,
            projectId,
            taskLabel,
            "Test Task",
            "Test Description",
            Priority.Medium,
            "Testing",
            1,
            Complexity.Medium,
            "Test prompt",
            "DONE",
            maxIterations: 10).Value;

        _dbContext.Projects.Add(project);
        _dbContext.Tasks.Add(task);

        return (projectId, taskId);
    }

    /// <summary>Adds a raw TaskExecution row directly (bypassing Task.RecordExecution) — all these endpoints read
    ///     TaskExecutions via a join, not via the Task aggregate's in-memory collection, so this is what they
    ///     actually see. Does not call SaveChanges.</summary>
    private TaskExecution SeedExecution(Guid taskId, Guid sessionId, string? modelId, int inputTokens, int outputTokens)
    {
        var execution = new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = 1,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ModelId = modelId
        };
        _dbContext.TaskExecutions.Add(execution);
        return execution;
    }

    /// <summary>Adds a second Task to an already-seeded project and returns its id. Does not call SaveChanges.</summary>
    private Guid SeedAdditionalTask(Guid projectId, string taskLabel)
    {
        var taskId = Guid.NewGuid();
        var task = DomainTask.Create(
            taskId,
            projectId,
            taskLabel,
            "Test Task",
            "Test Description",
            Priority.Medium,
            "Testing",
            1,
            Complexity.Medium,
            "Test prompt",
            "DONE",
            maxIterations: 10).Value;
        _dbContext.Tasks.Add(task);
        return taskId;
    }

    #region EstimateCostAsync

    [Fact]
    public async Task EstimateCostAsync_WithUnpricedModel_ReturnsFailureNamingTheModel()
    {
        // Falsifiability: reverting CostAnalyticsService.EstimateCostAsync's `if (!_pricing.Models.TryGetValue(...))`
        // branch to the old silent-substitution behaviour (relabel to the first configured entry) turns this red —
        // the result would be a success carrying "claude-sonnet-4-20250514" instead of a failure.
        var sut = new CostAnalyticsService(_dbContext, PricingWithOneModel());

        var result = await sut.EstimateCostAsync("claude-sonnet-5", maxIterations: 10, estimatedPromptTokens: 4000);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("claude-sonnet-5");
    }

    [Fact]
    public async Task EstimateCostAsync_WithPricedModel_ReturnsTheCorrectEstimate()
    {
        // Falsifiability: swapping InputTokenPricePerMillion and OutputTokenPricePerMillion in
        // CostAnalyticsService.EstimateCostAsync's cost formula turns this red — EstimatedMinCost/EstimatedMaxCost
        // would come out as 0.081/0.27 instead of 0.189/0.63. Prompt tokens (1000) are deliberately different from
        // the 4000-token response-token default below, so the two prices can't cancel each other out symmetrically.
        var sut = new CostAnalyticsService(_dbContext, PricingWithOneModel());

        var result = await sut.EstimateCostAsync(PricedModelId, maxIterations: 10, estimatedPromptTokens: 1000);

        result.IsSuccess.Should().BeTrue();
        var estimate = result.Value;
        estimate.ModelId.Should().Be(PricedModelId);
        estimate.ModelDisplayName.Should().Be("Claude Sonnet 4");
        estimate.EstimatedResponseTokens.Should().Be(4000); // no TaskExecutions in the DB -> the 4000-token default
        estimate.EstimatedMinCost.Should().Be(0.189m); // 3 iterations (30% of 10) * (1000*3 + 4000*15)/1_000_000
        estimate.EstimatedMaxCost.Should().Be(0.63m); // 10 iterations * (1000*3 + 4000*15)/1_000_000
    }

    #endregion

    #region GetSummaryAsync — per-model grouping

    [Fact]
    public async Task GetSummaryAsync_TwoExecutionsOnDifferentModels_PricesEachGroupAtItsOwnRate()
    {
        // Falsifiability: this is the exact defect the review flagged — CalculateCostFromExecutions priced the
        // *combined* token total at whichever model happened to be first in _pricing.Models. Reverting
        // CostAnalyticsService.PriceGroups to sum tokens first and price the total once (at ModelA's rate, first in
        // the dictionary here) turns this red: it would report 0.0495 instead of 0.0385.
        var (_, taskId) = SeedProjectWithTask();
        SeedExecution(taskId, Guid.NewGuid(), ModelA, inputTokens: 1000, outputTokens: 2000); // 0.003 + 0.03 = 0.033
        SeedExecution(taskId, Guid.NewGuid(), ModelB, inputTokens: 500, outputTokens: 1000); // 0.0005 + 0.005 = 0.0055
        await _dbContext.SaveChangesAsync();

        var sut = new CostAnalyticsService(_dbContext, PricingWithTwoModels());

        var result = await sut.GetSummaryAsync();

        result.TotalInputTokens.Should().Be(1500);
        result.TotalOutputTokens.Should().Be(3000);
        result.TotalExecutions.Should().Be(2);
        result.TotalCost.Should().Be(0.0385m); // 0.033 (model-a) + 0.0055 (model-b), priced separately
        result.Excluded.Should().Be(ExcludedCostDto.None);
    }

    [Fact]
    public async Task GetSummaryAsync_WithNullModelId_ExcludesAsUnattributed_AndDoesNotFailOrPriceIt()
    {
        // Falsifiability: removing the `if (group.ModelId is null)` branch in PriceGroups turns this red — not by
        // mispricing, but by throwing ArgumentNullException out of Dictionary<string,_>.TryGetValue(null), because
        // TaskExecution.ModelId is genuinely null here, the same way ExecuteTaskCommandHandler leaves it today.
        var (_, taskId) = SeedProjectWithTask();
        SeedExecution(taskId, Guid.NewGuid(), ModelA, inputTokens: 1000, outputTokens: 2000); // priced: 0.033
        SeedExecution(taskId, Guid.NewGuid(), modelId: null, inputTokens: 700, outputTokens: 300); // unattributed
        await _dbContext.SaveChangesAsync();

        var sut = new CostAnalyticsService(_dbContext, PricingWithTwoModels());

        var result = await sut.GetSummaryAsync();

        result.TotalCost.Should().Be(0.033m); // the unattributed row contributes nothing — not priced at model-a's rate either
        result.Excluded.UnattributedInputTokens.Should().Be(700);
        result.Excluded.UnattributedOutputTokens.Should().Be(300);
        result.Excluded.UnattributedExecutionCount.Should().Be(1);
        result.Excluded.UnpricedExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_WithUnknownModelId_ExcludesAsUnpriced_AndListsTheModelId()
    {
        // Falsifiability: reverting PriceGroups' `if (!_pricing.Models.TryGetValue(...))` branch to the original
        // defect (fold into _pricing.Models.Values.FirstOrDefault()'s rate) turns this red — TotalCost would
        // include the unpriced row's tokens priced at model-a's rate (0.033 + 0.0018 = 0.0348) instead of excluding
        // them (0.033).
        var (_, taskId) = SeedProjectWithTask();
        SeedExecution(taskId, Guid.NewGuid(), ModelA, inputTokens: 1000, outputTokens: 2000); // priced: 0.033
        SeedExecution(taskId, Guid.NewGuid(), "model-unknown", inputTokens: 400, outputTokens: 600); // unpriced
        await _dbContext.SaveChangesAsync();

        var sut = new CostAnalyticsService(_dbContext, PricingWithTwoModels());

        var result = await sut.GetSummaryAsync();

        result.TotalCost.Should().Be(0.033m);
        result.Excluded.UnpricedInputTokens.Should().Be(400);
        result.Excluded.UnpricedOutputTokens.Should().Be(600);
        result.Excluded.UnpricedExecutionCount.Should().Be(1);
        result.Excluded.UnpricedModelIds.Should().ContainSingle().Which.Should().Be("model-unknown");
        result.Excluded.UnattributedExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_StatesWhatItCovers()
    {
        // Falsifiability: clearing CostAnalyticsService.Scope (or never assigning it onto the DTO) turns this red.
        var sut = new CostAnalyticsService(_dbContext, PricingWithOneModel());

        var result = await sut.GetSummaryAsync();

        result.Scope.Should().NotBeNullOrWhiteSpace();
        result.Scope.Should().Contain("TaskExecution").And.Contain("Daedalus.Agents");
    }

    #endregion

    #region GetCostsByProjectAsync / GetCostsByProjectIdAsync / GetCostsBySessionIdAsync — per-model grouping

    [Fact]
    public async Task GetCostsByProjectAsync_TwoModelsInSameProject_PricesEachGroupSeparately()
    {
        // Falsifiability: same defect as GetSummaryAsync's, at a different call site — GetCostsByProjectAsync used
        // to group by project only (ignoring ModelId entirely) before calling the old CalculateCostFromExecutions.
        // Collapsing the ModelId key out of the GroupBy below reproduces that: the project's two model groups would
        // blend into 1500/3000 combined tokens before PriceGroups ever saw them, losing per-model attribution.
        var (projectId, taskA) = SeedProjectWithTask("TASK-1");
        var taskB = SeedAdditionalTask(projectId, "TASK-2");

        SeedExecution(taskA, Guid.NewGuid(), ModelA, inputTokens: 1000, outputTokens: 2000); // 0.033
        SeedExecution(taskB, Guid.NewGuid(), ModelB, inputTokens: 500, outputTokens: 1000); // 0.0055
        await _dbContext.SaveChangesAsync();

        var sut = new CostAnalyticsService(_dbContext, PricingWithTwoModels());

        var result = await sut.GetCostsByProjectAsync();

        var projectCost = result.Should().ContainSingle().Subject;
        projectCost.ProjectId.Should().Be(projectId);
        projectCost.InputTokens.Should().Be(1500);
        projectCost.OutputTokens.Should().Be(3000);
        projectCost.ExecutionCount.Should().Be(2);
        projectCost.EstimatedCost.Should().Be(0.0385m);
        projectCost.Excluded.Should().Be(ExcludedCostDto.None);
    }

    [Fact]
    public async Task GetCostsByProjectIdAsync_TwoModelsOnSameTask_PricesEachGroupSeparately()
    {
        // Falsifiability: GetCostsByProjectIdAsync used to group by task only (ignoring ModelId) before calling the
        // old CalculateCostFromExecutions on the task's combined tokens. Dropping x.Execution.ModelId from the
        // GroupBy key below reproduces exactly that.
        var (_, taskId) = SeedProjectWithTask("TASK-1");
        SeedExecution(taskId, Guid.NewGuid(), ModelA, inputTokens: 1000, outputTokens: 2000); // 0.033
        SeedExecution(taskId, Guid.NewGuid(), ModelB, inputTokens: 500, outputTokens: 1000); // 0.0055
        await _dbContext.SaveChangesAsync();
        var projectId = await _dbContext.Tasks.Where(t => t.Id == taskId).Select(t => t.ProjectId).SingleAsync();

        var sut = new CostAnalyticsService(_dbContext, PricingWithTwoModels());

        var result = await sut.GetCostsByProjectIdAsync(projectId);

        var taskCost = result.Should().ContainSingle().Subject;
        taskCost.TaskId.Should().Be(taskId);
        taskCost.InputTokens.Should().Be(1500);
        taskCost.OutputTokens.Should().Be(3000);
        taskCost.IterationCount.Should().Be(2);
        taskCost.EstimatedCost.Should().Be(0.0385m);
        taskCost.Excluded.Should().Be(ExcludedCostDto.None);
    }

    [Fact]
    public async Task GetCostsBySessionIdAsync_TwoModelsInSameSession_PricesEachGroupSeparately()
    {
        // Falsifiability: same shape of defect again — GetCostsBySessionIdAsync used to group by task only
        // (ignoring ModelId) before calling the old CalculateCostFromExecutions on the combined tokens.
        var (_, taskId) = SeedProjectWithTask("TASK-1");
        var sessionId = Guid.NewGuid();
        SeedExecution(taskId, sessionId, ModelA, inputTokens: 1000, outputTokens: 2000); // 0.033
        SeedExecution(taskId, sessionId, ModelB, inputTokens: 500, outputTokens: 1000); // 0.0055
        await _dbContext.SaveChangesAsync();

        var sut = new CostAnalyticsService(_dbContext, PricingWithTwoModels());

        var result = await sut.GetCostsBySessionIdAsync(sessionId);

        var taskCost = result.Should().ContainSingle().Subject;
        taskCost.TaskId.Should().Be(taskId);
        taskCost.InputTokens.Should().Be(1500);
        taskCost.OutputTokens.Should().Be(3000);
        taskCost.IterationCount.Should().Be(2);
        taskCost.EstimatedCost.Should().Be(0.0385m);
        taskCost.Excluded.Should().Be(ExcludedCostDto.None);
    }

    #endregion
}
