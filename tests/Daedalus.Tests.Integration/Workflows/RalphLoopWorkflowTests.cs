using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DomainTaskStatus = Daedalus.Domain.Entities.TaskStatus;
using SystemTask = System.Threading.Tasks.Task;

#pragma warning disable MA0002

namespace Daedalus.Tests.Integration.Workflows;

/// <summary>
///     Integration tests for complete Ralph Loop workflows with real database.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class RalphLoopWorkflowTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ILogger<TaskAssignmentService>
        _assignmentLogger = Substitute.For<ILogger<TaskAssignmentService>>();

    private readonly IContext7DocumentationInjector
        _context7Injector = Substitute.For<IContext7DocumentationInjector>();

    private readonly ILlmService _llmService = Substitute.For<ILlmService>();
    private readonly ILogger<RalphLoopService> _loopLogger = Substitute.For<ILogger<RalphLoopService>>();
    private readonly IMcpAgentSelector _mcpAgentSelector = Substitute.For<IMcpAgentSelector>();
    private readonly IPromptBuilder _promptBuilder = Substitute.For<IPromptBuilder>();

    private readonly ILogger<ExecutionSessionRepository> _sessionLogger =
        Substitute.For<ILogger<ExecutionSessionRepository>>();

    private readonly ILogger<TaskRepository> _taskLogger = Substitute.For<ILogger<TaskRepository>>();
    private TaskAssignmentService _assignmentService = null!;
    private ApplicationDbContext _dbContext = null!;
    private RalphLoopService _loopService = null!;
    private ExecutionSessionRepository _sessionRepository = null!;
    private TaskRepository _taskRepository = null!;

    public async SystemTask InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _taskRepository = new TaskRepository(_dbContext, _taskLogger);
        _sessionRepository = new ExecutionSessionRepository(_dbContext, _sessionLogger);
        _assignmentService = new TaskAssignmentService(_taskRepository, _assignmentLogger);

        // Create default project for tests
        var project = IntegrationTestFactory.CreateProject(IntegrationTestFactory.GetDefaultProjectId());
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        var loopConfig = new RalphLoopConfiguration { IterationDelayMs = 10, MaxConsecutiveFailures = 3 };

        _promptBuilder
            .InitializeContextAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                Result.Success(new PromptContext
                {
                    TaskId = callInfo.ArgAt<Guid>(0),
                    SessionId = callInfo.ArgAt<Guid>(1),
                    OriginalPrompt = callInfo.ArgAt<string>(2),
                    CompletionPromise = callInfo.ArgAt<string>(3),
                    AccumulatedLearnings = callInfo.ArgAt<string>(4)
                }));
        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result.Success(callInfo.ArgAt<PromptContext>(0).OriginalPrompt));
        _promptBuilder
            .RecordIterationResultAsync(Arg.Any<PromptContext>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<TimeSpan>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _promptBuilder
            .PersistContextAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Setup MCP service mocks
        _mcpAgentSelector
            .FindAgentsForPromptAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<AgentMetadata>>([]));

        _context7Injector
            .GetDocumentationContextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(""));

        _loopService = new RalphLoopService(_llmService, _taskRepository, _promptBuilder,
            _loopLogger, loopConfig, _mcpAgentSelector, _context7Injector);
    }

    public async SystemTask DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }

    #region Task Execution Tracking Tests

    [Fact]
    public async SystemTask Workflow_TrackAllExecutionDetails()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        var taskId = Guid.NewGuid();
        var task = IntegrationTestFactory.CreateTask(taskId, prompt: "Generate", completionPromise: "DONE",
            maxIterations: 10);
        await _taskRepository.AddAsync(task, CancellationToken.None);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("DONE"));

        // Act
        var claimResult = await _assignmentService.GetNextAvailableTaskAsync(sessionId, CancellationToken.None);
        var claimedTask = claimResult.Value!;
        await _loopService.ExecuteAsync(claimedTask, sessionId, CancellationToken.None);

        // Assert
        var retrievedTask = await _taskRepository.GetByIdAsync(taskId, CancellationToken.None);
        retrievedTask.Value.Executions.Should().HaveCount(1);

        var execution = retrievedTask.Value.Executions[0];
        execution.TaskId.Should().Be(taskId);
        execution.SessionId.Should().Be(sessionId);
        execution.IterationNumber.Should().Be(1);
        execution.Prompt.Should().Be("Generate");
        execution.CompletionPromiseFound.Should().BeTrue();
    }

    #endregion

    #region Complete Workflow Tests

    [Fact]
    public async SystemTask Workflow_CompleteTaskSuccessfully()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        var taskId = Guid.NewGuid();
        var task = IntegrationTestFactory.CreateTask(taskId, prompt: "Generate a greeting", completionPromise: "Hello",
            maxIterations: 10);
        await _taskRepository.AddAsync(task, CancellationToken.None);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Hello, World!"));

        // Act 1 - Assign task to session
        var claimResult = await _assignmentService.GetNextAvailableTaskAsync(sessionId, CancellationToken.None);

        claimResult.IsSuccess.Should().BeTrue();
        var claimedTask = claimResult.Value;
        claimedTask!.Status.Should().Be(DomainTaskStatus.InProgress);

        // Act 2 - Execute Ralph loop
        var executeResult = await _loopService.ExecuteAsync(claimedTask, sessionId, CancellationToken.None);

        executeResult.IsSuccess.Should().BeTrue();
        claimedTask.Status.Should().Be(DomainTaskStatus.Completed);
        claimedTask.Result.Should().Be("Hello, World!");
        claimedTask.IterationCount.Should().Be(1);

        // Act 3 - Verify persistence
        var retrievedTask = await _taskRepository.GetByIdAsync(taskId, CancellationToken.None);

        retrievedTask.IsSuccess.Should().BeTrue();
        retrievedTask.Value.Status.Should().Be(DomainTaskStatus.Completed);
    }

    [Fact]
    public async SystemTask Workflow_MultipleIterationsUntilSuccess()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        var taskId = Guid.NewGuid();
        var task = IntegrationTestFactory.CreateTask(taskId, prompt: "Generate code", completionPromise: "function",
            maxIterations: 10);
        await _taskRepository.AddAsync(task, CancellationToken.None);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("class Foo { }"), Result.Success("function bar() { }"));

        // Act
        var claimResult = await _assignmentService.GetNextAvailableTaskAsync(sessionId, CancellationToken.None);
        var claimedTask = claimResult.Value!;

        var executeResult = await _loopService.ExecuteAsync(claimedTask, sessionId, CancellationToken.None);

        // Assert
        executeResult.IsSuccess.Should().BeTrue();
        claimedTask.Status.Should().Be(DomainTaskStatus.Completed);
        claimedTask.IterationCount.Should().Be(2);
        claimedTask.Result.Should().Contain("function");
    }

    [Fact]
    public async SystemTask Workflow_TaskFailsAfterMaxIterations()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        var taskId = Guid.NewGuid();
        var task = IntegrationTestFactory.CreateTask(taskId, prompt: "Generate success", completionPromise: "SUCCESS",
            maxIterations: 3);
        await _taskRepository.AddAsync(task, CancellationToken.None);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Failure"));

        // Act
        var claimResult = await _assignmentService.GetNextAvailableTaskAsync(sessionId, CancellationToken.None);
        var claimedTask = claimResult.Value!;

        var executeResult = await _loopService.ExecuteAsync(claimedTask, sessionId, CancellationToken.None);

        // Assert
        executeResult.IsSuccess.Should().BeTrue();
        claimedTask.Status.Should().Be(DomainTaskStatus.Failed);
        claimedTask.IterationCount.Should().Be(3);
        claimedTask.Result.Should().BeNull();

        var retrievedTask = await _taskRepository.GetByIdAsync(taskId, CancellationToken.None);
        retrievedTask.Value.Status.Should().Be(DomainTaskStatus.Failed);
    }

    [Fact]
    public async SystemTask Workflow_MultipleSessionsWorkingInParallel()
    {
        // Arrange
        var session1 = ExecutionSession.Create(Guid.NewGuid(), "worker-001").Value;
        var session2 = ExecutionSession.Create(Guid.NewGuid(), "worker-002").Value;
        await _sessionRepository.AddAsync(session1, CancellationToken.None);
        await _sessionRepository.AddAsync(session2, CancellationToken.None);

        var task1 = IntegrationTestFactory.CreateTask(prompt: "Task 1", completionPromise: "DONE", maxIterations: 5);
        var task2 = IntegrationTestFactory.CreateTask(prompt: "Task 2", completionPromise: "COMPLETE",
            maxIterations: 5);
        await _taskRepository.AddAsync(task1, CancellationToken.None);
        await _taskRepository.AddAsync(task2, CancellationToken.None);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prompt = callInfo.ArgAt<string>(0);
                return prompt.Contains("Task 1") || prompt.Contains("test prompt")
                    ? Result.Success("DONE")
                    : Result.Success("COMPLETE");
            });

        // Act
        var claim1 = await _assignmentService.GetNextAvailableTaskAsync(session1.Id, CancellationToken.None);
        var claim2 = await _assignmentService.GetNextAvailableTaskAsync(session2.Id, CancellationToken.None);

        // Assert
        claim1.Value!.Id.Should().Be(task1.Id);
        claim2.Value!.Id.Should().Be(task2.Id);
        claim1.Value.CurrentSessionId.Should().Be(session1.Id);
        claim2.Value.CurrentSessionId.Should().Be(session2.Id);
    }

    [Fact]
    public async SystemTask Workflow_SessionRecoveryFromStaleState()
    {
        // Arrange
        var session = ExecutionSession.Create(Guid.NewGuid(), "worker-001").Value;
        await _sessionRepository.AddAsync(session, CancellationToken.None);

        var task = IntegrationTestFactory.CreateTask(prompt: "Test task", completionPromise: "DONE", maxIterations: 5);
        await _taskRepository.AddAsync(task, CancellationToken.None);

        var claimResult = await _assignmentService.GetNextAvailableTaskAsync(session.Id, CancellationToken.None);
        var claimedTask = claimResult.Value!;
        claimedTask.Status.Should().Be(DomainTaskStatus.InProgress);

        session.Heartbeat(DateTime.UtcNow.AddMinutes(-10));
        await _sessionRepository.UpdateAsync(session, CancellationToken.None);

        _dbContext.ChangeTracker.Clear();

        // Act
        var reclaimResult = await _assignmentService.ReclaimStaleTasksAsync(CancellationToken.None);

        // Assert
        reclaimResult.IsSuccess.Should().BeTrue();

        var reclaimedTask = await _taskRepository.GetByIdAsync(task.Id, CancellationToken.None);
        reclaimedTask.Value.Status.Should().Be(DomainTaskStatus.Abandoned);
        reclaimedTask.Value.CurrentSessionId.Should().BeNull();
    }

    #endregion
}
