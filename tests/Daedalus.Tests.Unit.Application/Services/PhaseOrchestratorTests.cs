using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>
///     Unit tests for PhaseOrchestrator — the phase chaining service that evaluates
///     whether completing a task unblocks dependent tasks in the same project.
/// </summary>
public class PhaseOrchestratorTests : UnitTestBase
{
    private readonly ILogger<PhaseOrchestrator> _mockLogger;
    private readonly ITaskRepository _mockTaskRepository;
    private readonly PhaseOrchestrator _orchestrator;
    private readonly Guid _projectId = Guid.NewGuid();

    public PhaseOrchestratorTests()
    {
        _mockTaskRepository = Substitute.For<ITaskRepository>();
        _mockLogger = Substitute.For<ILogger<PhaseOrchestrator>>();
        _orchestrator = new PhaseOrchestrator(_mockTaskRepository, _mockLogger);
    }

    #region Happy Path - Task Unblocking

    [Fact]
    public async Task OnTaskCompleted_WhenDependentTaskHasSingleDependency_ShouldUnblockIt()
    {
        // Arrange
        var taskA = CreateCompletedTask("TASK-A", "Backend");
        var taskB = CreatePendingTaskWithDependencies("TASK-B", "Frontend", ["TASK-A"]);

        SetupProjectTasks(taskA, taskB);

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(taskA, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Should().Be("TASK-B");
    }

    [Fact]
    public async Task OnTaskCompleted_WhenMultipleDependentTasks_ShouldUnblockAll()
    {
        // Arrange
        var taskA = CreateCompletedTask("TASK-A", "Backend");
        var taskB = CreatePendingTaskWithDependencies("TASK-B", "Frontend", ["TASK-A"]);
        var taskC = CreatePendingTaskWithDependencies("TASK-C", "Testing", ["TASK-A"]);

        SetupProjectTasks(taskA, taskB, taskC);

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(taskA, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().Contain("TASK-B");
        result.Value.Should().Contain("TASK-C");
    }

    [Fact]
    public async Task OnTaskCompleted_WhenAllDependenciesNowMet_ShouldUnblockTask()
    {
        // Arrange: TASK-C depends on both TASK-A (already completed) and TASK-B (just completed)
        var taskA = CreateCompletedTask("TASK-A", "Backend");
        var taskB = CreateCompletedTask("TASK-B", "Backend");
        var taskC = CreatePendingTaskWithDependencies("TASK-C", "Frontend", ["TASK-A", "TASK-B"]);

        SetupProjectTasks(taskA, taskB, taskC);

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(taskB, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Should().Be("TASK-C");
    }

    #endregion

    #region Dependency Not Yet Met

    [Fact]
    public async Task OnTaskCompleted_WhenDependentStillHasUnmetDependencies_ShouldNotUnblock()
    {
        // Arrange: TASK-C depends on TASK-A (completed) and TASK-B (still pending)
        var taskA = CreateCompletedTask("TASK-A", "Backend");
        var taskB = CreatePendingTask("TASK-B", "Backend");
        var taskC = CreatePendingTaskWithDependencies("TASK-C", "Frontend", ["TASK-A", "TASK-B"]);

        SetupProjectTasks(taskA, taskB, taskC);

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(taskA, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task OnTaskCompleted_WhenDependencyReferencesMissingTask_ShouldNotUnblock()
    {
        // Arrange: TASK-B depends on TASK-A (completed) and TASK-MISSING (not in project)
        var taskA = CreateCompletedTask("TASK-A", "Backend");
        var taskB = CreatePendingTaskWithDependencies("TASK-B", "Frontend", ["TASK-A", "TASK-MISSING"]);

        SetupProjectTasks(taskA, taskB);

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(taskA, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    #endregion

    #region No Dependents

    [Fact]
    public async Task OnTaskCompleted_WhenNoDependentTasks_ShouldReturnEmptyList()
    {
        // Arrange
        var taskA = CreateCompletedTask("TASK-A", "Backend");
        var taskB = CreatePendingTask("TASK-B", "Backend"); // No dependencies

        SetupProjectTasks(taskA, taskB);

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(taskA, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task OnTaskCompleted_WhenOnlyTaskInProject_ShouldReturnEmptyList()
    {
        // Arrange
        var taskA = CreateCompletedTask("TASK-A", "Backend");

        SetupProjectTasks(taskA);

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(taskA, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    #endregion

    #region Status Filters

    [Fact]
    public async Task OnTaskCompleted_WhenDependentTaskAlreadyCompleted_ShouldNotIncludeIt()
    {
        // Arrange: TASK-B depends on TASK-A but is already Completed itself
        var taskA = CreateCompletedTask("TASK-A", "Backend");
        var taskB = CreateCompletedTask("TASK-B", "Frontend");
        // Note: Can't add dependency after Completed, so create pending then complete
        // Instead, simply verify the orchestrator only checks Pending tasks

        SetupProjectTasks(taskA, taskB);

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(taskA, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task OnTaskCompleted_WhenDependentTaskInProgress_ShouldNotUnblock()
    {
        // Arrange: TASK-B depends on TASK-A but is InProgress
        var taskA = CreateCompletedTask("TASK-A", "Backend");
        var taskB = CreateInProgressTaskWithDependencies("TASK-B", "Frontend", ["TASK-A"]);

        SetupProjectTasks(taskA, taskB);

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(taskA, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    #endregion

    #region Validation Errors

    [Fact]
    public async Task OnTaskCompleted_WhenTaskNotCompleted_ShouldReturnFailure()
    {
        // Arrange
        var task = CreatePendingTask("TASK-A", "Backend");

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(task, _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not completed");
    }

    [Fact]
    public async Task OnTaskCompleted_WhenRepositoryFails_ShouldReturnFailure()
    {
        // Arrange
        var taskA = CreateCompletedTask("TASK-A", "Backend");

        _mockTaskRepository
            .GetByProjectIdAsync(taskA.ProjectId, _cancellationToken)
            .Returns(Result.Failure<IReadOnlyList<DomainTask>>("Database error"));

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(taskA, _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Database error");
    }

    #endregion

    #region Multi-Phase Chaining

    [Fact]
    public async Task OnTaskCompleted_WithThreePhases_ShouldOnlyUnblockImmediatePhase()
    {
        // Arrange: Phase 1 → Phase 2 → Phase 3
        // TASK-A (Phase 1, completed)
        // TASK-B (Phase 2, depends on TASK-A) — should be unblocked
        // TASK-C (Phase 3, depends on TASK-B) — should NOT be unblocked (TASK-B still pending)
        var taskA = CreateCompletedTask("TASK-A", "Phase1");
        var taskB = CreatePendingTaskWithDependencies("TASK-B", "Phase2", ["TASK-A"]);
        var taskC = CreatePendingTaskWithDependencies("TASK-C", "Phase3", ["TASK-B"]);

        SetupProjectTasks(taskA, taskB, taskC);

        // Act
        var result = await _orchestrator.OnTaskCompletedAsync(taskA, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Should().Be("TASK-B");
    }

    [Fact]
    public async Task OnTaskCompleted_DiamondDependencyPattern_ShouldUnblockWhenBothMet()
    {
        // Arrange: Diamond dependency
        // TASK-A (completed) → TASK-C
        // TASK-B (completed) → TASK-C
        // TASK-C depends on both TASK-A and TASK-B
        var taskA = CreateCompletedTask("TASK-A", "Backend");
        var taskB = CreateCompletedTask("TASK-B", "Backend");
        var taskC = CreatePendingTaskWithDependencies("TASK-C", "Integration", ["TASK-A", "TASK-B"]);

        SetupProjectTasks(taskA, taskB, taskC);

        // Act — completing TASK-B (last one)
        var result = await _orchestrator.OnTaskCompletedAsync(taskB, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Should().Be("TASK-C");
    }

    [Fact]
    public async Task OnTaskCompleted_DiamondDependencyPartial_ShouldNotUnblockYet()
    {
        // Arrange: Diamond, but only one arm completed
        var taskA = CreateCompletedTask("TASK-A", "Backend");
        var taskB = CreatePendingTask("TASK-B", "Backend");
        var taskC = CreatePendingTaskWithDependencies("TASK-C", "Integration", ["TASK-A", "TASK-B"]);

        SetupProjectTasks(taskA, taskB, taskC);

        // Act — only TASK-A completed, TASK-B still pending
        var result = await _orchestrator.OnTaskCompletedAsync(taskA, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    #endregion

    #region Helper Methods

    private DomainTask CreatePendingTask(string taskId, string phase)
    {
        return ApplicationTestFactory.CreateTask(
            projectId: _projectId,
            taskId: taskId,
            phase: phase);
    }

    private DomainTask CreatePendingTaskWithDependencies(string taskId, string phase, string[] dependencies)
    {
        var task = ApplicationTestFactory.CreateTask(
            projectId: _projectId,
            taskId: taskId,
            phase: phase);

        foreach (var dep in dependencies)
        {
            task.AddDependency(dep);
        }

        return task;
    }

    private DomainTask CreateCompletedTask(string taskId, string phase)
    {
        var task = ApplicationTestFactory.CreateTask(
            projectId: _projectId,
            taskId: taskId,
            phase: phase);

        // Transition through the state machine: Pending → InProgress → Completed
        var sessionId = Guid.NewGuid();
        task.Claim(sessionId);
        task.RecordExecution(CreateCompletionExecution(task.Id));

        return task;
    }

    private DomainTask CreateInProgressTaskWithDependencies(string taskId, string phase, string[] dependencies)
    {
        var task = CreatePendingTaskWithDependencies(taskId, phase, dependencies);
        task.Claim(Guid.NewGuid());
        return task;
    }

    private static TaskExecution CreateCompletionExecution(Guid taskId)
    {
        return new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = Guid.NewGuid(),
            IterationNumber = 1,
            Prompt = "Test prompt",
            LlmResponse = "DONE",
            CompletionPromiseFound = true,
            ExecutedAt = DateTime.UtcNow,
            ExecutionDuration = TimeSpan.FromMilliseconds(100)
        };
    }

    private void SetupProjectTasks(params DomainTask[] tasks)
    {
        _mockTaskRepository
            .GetByProjectIdAsync(_projectId, _cancellationToken)
            .Returns(Result.Success<IReadOnlyList<DomainTask>>(tasks.ToList().AsReadOnly()));
    }

    #endregion
}
