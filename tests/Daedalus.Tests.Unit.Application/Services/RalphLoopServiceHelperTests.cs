using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>
///     Tests for RalphLoopService helper methods and optional service integrations
///     (git checkpoints, loopback evaluation, plan file management).
/// </summary>
public class RalphLoopServiceHelperTests : UnitTestBase
{
    private readonly RalphLoopConfiguration _config;
    private readonly IContext7DocumentationInjector _context7Injector;
    private readonly IGitWorkflowService _gitWorkflowService;
    private readonly ILlmService _llmService;
    private readonly ILogger<RalphLoopService> _logger;
    private readonly ILoopbackEvaluator _loopbackEvaluator;
    private readonly IMcpAgentSelector _mcpAgentSelector;
    private readonly IPromptBuilder _promptBuilder;
    private readonly ITaskRepository _taskRepository;

    public RalphLoopServiceHelperTests()
    {
        _llmService = Substitute.For<ILlmService>();
        _taskRepository = Substitute.For<ITaskRepository>();
        _promptBuilder = Substitute.For<IPromptBuilder>();
        _logger = Substitute.For<ILogger<RalphLoopService>>();
        _mcpAgentSelector = Substitute.For<IMcpAgentSelector>();
        _context7Injector = Substitute.For<IContext7DocumentationInjector>();
        _gitWorkflowService = Substitute.For<IGitWorkflowService>();
        _loopbackEvaluator = Substitute.For<ILoopbackEvaluator>();
        _config = CreateConfigWithAllFeatures();

        SetupDefaultMocks();
    }

    private void SetupLlmToComplete()
    {
        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Response with DONE"));
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

        _gitWorkflowService
            .CommitAfterSuccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("commit-sha"));

        _gitWorkflowService
            .TagOnCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("v1.0"));

        _loopbackEvaluator
            .EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoopbackResult
            {
                BuildSucceeded = true, TestsPassed = true, TestsPassed_Count = 10, TestsFailed_Count = 0
            }));
    }

    private static RalphLoopConfiguration CreateConfigWithAllFeatures()
    {
        return new RalphLoopConfiguration
        {
            IterationDelayMs = 0,
            MaxConsecutiveFailures = 3,
            EnableGitCheckpoints = true,
            EnableLoopbackEvaluation = true,
            WorkspacePath = "/tmp/test-workspace",
            PlanRegenerationThreshold = 3
        };
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
            _context7Injector,
            _gitWorkflowService,
            _loopbackEvaluator);
    }

    [Fact]
    public async Task ExecuteAsync_WithGitEnabled_CallsGitCommitAfterSuccess()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        SetupLlmToComplete();

        // Act
        var result = await service.ExecuteAsync(task, sessionId, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Completed);
        await _gitWorkflowService.Received()
            .CommitAfterSuccessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithGitEnabled_CallsTagOnCompletion()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        SetupLlmToComplete();

        // Act
        var result = await service.ExecuteAsync(task, sessionId, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _gitWorkflowService.Received()
            .TagOnCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithGitDisabled_DoesNotCallGitServices()
    {
        // Arrange
        _config.EnableGitCheckpoints = false;
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        SetupLlmToComplete();

        // Act
        var result = await service.ExecuteAsync(task, sessionId, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _gitWorkflowService.DidNotReceive()
            .CommitAfterSuccessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithLoopbackEnabled_CallsEvaluateAsync()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        // First iteration doesn't find promise (triggers loopback), second completes
        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Result.Success("No promise yet"),
                Result.Success("Response with DONE"));

        // Act
        var result = await service.ExecuteAsync(task, sessionId, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _loopbackEvaluator.Received()
            .EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithLoopbackDisabled_DoesNotCallEvaluator()
    {
        // Arrange
        _config.EnableLoopbackEvaluation = false;
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        SetupLlmToComplete();

        // Act
        var result = await service.ExecuteAsync(task, sessionId, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _loopbackEvaluator.DidNotReceive()
            .EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_GitCommitFailure_DoesNotStopExecution()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        _gitWorkflowService
            .CommitAfterSuccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("Git commit failed"));

        SetupLlmToComplete();

        // Act
        var result = await service.ExecuteAsync(task, sessionId, _cancellationToken);

        // Assert - Execution should succeed even if git commit fails
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoWorkspacePath_SkipsGitAndLoopback()
    {
        // Arrange
        _config.WorkspacePath = string.Empty;
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        SetupLlmToComplete();

        // Act
        var result = await service.ExecuteAsync(task, sessionId, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _gitWorkflowService.DidNotReceive()
            .CommitAfterSuccessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _loopbackEvaluator.DidNotReceive()
            .EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
