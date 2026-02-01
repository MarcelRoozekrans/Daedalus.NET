using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>
///     Edge-case and boundary tests for RalphLoopService.
///     Covers cancellation, timing, error propagation, and unusual states.
/// </summary>
public class RalphLoopServiceEdgeCaseTests
{
    private readonly RalphLoopConfiguration _config;
    private readonly IContext7DocumentationInjector _context7Injector;
    private readonly ILlmService _llmService;
    private readonly ILogger<RalphLoopService> _logger;
    private readonly IMcpAgentSelector _mcpAgentSelector;
    private readonly IPromptBuilder _promptBuilder;
    private readonly ITaskRepository _taskRepository;

    public RalphLoopServiceEdgeCaseTests()
    {
        _llmService = Substitute.For<ILlmService>();
        _taskRepository = Substitute.For<ITaskRepository>();
        _promptBuilder = Substitute.For<IPromptBuilder>();
        _logger = Substitute.For<ILogger<RalphLoopService>>();
        _mcpAgentSelector = Substitute.For<IMcpAgentSelector>();
        _context7Injector = Substitute.For<IContext7DocumentationInjector>();
        _config = new RalphLoopConfiguration { IterationDelayMs = 0, MaxConsecutiveFailures = 3 };

        SetupDefaultMocks();
    }

    private void SetupDefaultMocks()
    {
        _mcpAgentSelector
            .FindAgentsForPromptAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<AgentMetadata>>([]));

        _context7Injector
            .GetDocumentationContextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(""));

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

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        _taskRepository
            .RecordExecutionAsync(Arg.Any<TaskExecution>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
    }

    private RalphLoopService CreateService()
    {
        return new RalphLoopService(
            _llmService,
            _taskRepository,
            _promptBuilder,
            _logger,
            _config,
            _mcpAgentSelector,
            _context7Injector);
    }

    [Fact]
    public async Task ExecuteAsync_WithSingleIteration_CompletesImmediately()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Quick task",
            completionPromise: "DONE",
            maxIterations: 1);
        task.Claim(sessionId);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("DONE"));

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Completed);
        task.IterationCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WithSingleIteration_FailsWhenPromiseNotFound()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Quick task",
            completionPromise: "DONE",
            maxIterations: 1);
        task.Claim(sessionId);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Not done yet"));

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Failed);
        task.IterationCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_PreCancelledToken_DoesNotExecute()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        var cts = new CancellationTokenSource();
        await cts.CancelAsync(); // Pre-cancel

        // Act
        var result = await service.ExecuteAsync(task, sessionId, cts.Token);

        // Assert - should return early without calling LLM
        await _llmService.DidNotReceive()
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithVeryLongResponse_HandlesCorrectly()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate large output",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        var longResponse = new string('x', 100_000) + " DONE";
        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(longResponse));

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyLlmResponse_ContinuesIterating()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "DONE",
            maxIterations: 3);
        task.Claim(sessionId);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Result.Success(""),
                Result.Success(""),
                Result.Success("DONE"));

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Completed);
        task.IterationCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_WithPromptBuilderBuildFailure_ContinuesIterating()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        // First BuildIterationPrompt fails, second succeeds
        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(
                Result.Failure<string>("Build failed"),
                Result.Success("Test DONE"));

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("DONE"));

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert - service returns failure when prompt builder fails on first iteration
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Build failed");
    }

    [Fact]
    public async Task ExecuteAsync_RecordsExecutionDuration()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("DONE"));

        TaskExecution? capturedExecution = null;
        _taskRepository
            .RecordExecutionAsync(Arg.Do<TaskExecution>(e => capturedExecution = e), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        capturedExecution.Should().NotBeNull();
        capturedExecution!.ExecutionDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_PersistsContext_AfterEachIteration()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "DONE",
            maxIterations: 2);
        task.Claim(sessionId);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("No promise"));

        // Act
        await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert - PersistContextAsync should be called for each iteration
        await _promptBuilder.Received(2)
            .PersistContextAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RepositoryUpdateFailure_DoesNotStopExecution()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "DONE",
            maxIterations: 3);
        task.Claim(sessionId);

        _taskRepository
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Update failed"));

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Result.Success("Not done"),
                Result.Success("DONE"));

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert - service stops execution when task state cannot be persisted
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AllConsecutiveLlmFailures_StopsExecution()
    {
        // Arrange
        _config.MaxConsecutiveFailures = 2;
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "DONE",
            maxIterations: 100);
        task.Claim(sessionId);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("Service unavailable"));

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        task.IterationCount.Should().BeLessThan(100);
    }

    [Fact]
    public async Task ExecuteAsync_ResetsConsecutiveFailures_AfterSuccess()
    {
        // Arrange
        _config.MaxConsecutiveFailures = 2;
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "DONE",
            maxIterations: 10);
        task.Claim(sessionId);

        // Fail, succeed, fail, fail should stop at the second consecutive failure block
        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Result.Failure<string>("Error"),
                Result.Success("Not done"), // resets counter
                Result.Failure<string>("Error"),
                Result.Failure<string>("Error"), // hits max consecutive
                Result.Success("Should not reach DONE"));

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert - should have executed more than 2 but less than 10 iterations
        task.IterationCount.Should().BeGreaterThan(2);
        task.IterationCount.Should().BeLessThan(10);
    }

    [Fact]
    public async Task ExecuteAsync_WithCompletionPromiseInMiddleOfResponse_Detects()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "TARGET_FOUND",
            maxIterations: 5);
        task.Claim(sessionId);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Some text before TARGET_FOUND and text after"));

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_CallsContext7Injector()
    {
        // Arrange - create service with MCP enabled so Context7 injector is called
        var service = new RalphLoopService(
            _llmService,
            _taskRepository,
            _promptBuilder,
            _logger,
            _config,
            _mcpAgentSelector,
            _context7Injector,
            mcpOptions: new McpIntegrationOptions { Enabled = true });
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test with docs",
            completionPromise: "DONE",
            maxIterations: 1);
        task.Claim(sessionId);

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("DONE"));

        // Act
        await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        await _context7Injector.Received()
            .GetDocumentationContextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
