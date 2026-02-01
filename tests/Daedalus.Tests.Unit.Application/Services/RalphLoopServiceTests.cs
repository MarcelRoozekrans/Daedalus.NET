using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>
///     Unit tests for RalphLoopService core execution loop.
/// </summary>
public class RalphLoopServiceTests
{
    private readonly RalphLoopConfiguration _config;
    private readonly IContext7DocumentationInjector _context7Injector;
    private readonly ILlmService _llmService;
    private readonly ILogger<RalphLoopService> _logger;
    private readonly IMcpAgentSelector _mcpAgentSelector;
    private readonly IPromptBuilder _promptBuilder;
    private readonly ITaskRepository _taskRepository;

    public RalphLoopServiceTests()
    {
        _llmService = Substitute.For<ILlmService>();
        _taskRepository = Substitute.For<ITaskRepository>();
        _promptBuilder = Substitute.For<IPromptBuilder>();
        _logger = Substitute.For<ILogger<RalphLoopService>>();
        _mcpAgentSelector = Substitute.For<IMcpAgentSelector>();
        _context7Injector = Substitute.For<IContext7DocumentationInjector>();
        _config = new RalphLoopConfiguration { IterationDelayMs = 0, MaxConsecutiveFailures = 3 };

        // Default happy-path setups
        _mcpAgentSelector
            .FindAgentsForPromptAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<AgentMetadata>>([]));

        _context7Injector
            .GetDocumentationContextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(""));
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
    public async Task ExecuteAsync_CompletesOnFirstIteration_WhenPromiseFoundImmediately()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        var context = new PromptContext
        {
            TaskId = task.Id,
            SessionId = sessionId,
            OriginalPrompt = task.Prompt,
            CompletionPromise = task.CompletionPromise
        };

        _promptBuilder
            .InitializeContextAsync(task.Id, sessionId, task.Prompt, task.CompletionPromise,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(context));

        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Generate code"));

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Here is the code. DONE"));

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

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_IteratesMultipleTimes_UntilPromiseFound()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 10);
        task.Claim(sessionId);

        var context = new PromptContext
        {
            TaskId = task.Id,
            SessionId = sessionId,
            OriginalPrompt = task.Prompt,
            CompletionPromise = task.CompletionPromise
        };

        _promptBuilder
            .InitializeContextAsync(task.Id, sessionId, task.Prompt, task.CompletionPromise,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(context));

        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Generate code"));

        // First two calls return without DONE, third returns with DONE
        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Result.Success("Attempt 1"),
                Result.Success("Attempt 2"),
                Result.Success("Attempt 3 DONE"));

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

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Completed);
        task.IterationCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_FailsAfterMaxIterations_WhenPromiseNeverFound()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 3);
        task.Claim(sessionId);

        var context = new PromptContext
        {
            TaskId = task.Id,
            SessionId = sessionId,
            OriginalPrompt = task.Prompt,
            CompletionPromise = task.CompletionPromise
        };

        _promptBuilder
            .InitializeContextAsync(task.Id, sessionId, task.Prompt, task.CompletionPromise,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(context));

        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Generate code"));

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("No promise here"));

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

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Failed);
        task.IterationCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_WhenInitializeContextFails()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE");
        task.Claim(sessionId);

        _promptBuilder
            .InitializeContextAsync(task.Id, sessionId, task.Prompt, task.CompletionPromise,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PromptContext>("Failed to initialize context"));

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Failed to initialize context");
    }

    [Fact]
    public async Task ExecuteAsync_HandlesLlmFailure_AndContinues()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 5);
        task.Claim(sessionId);

        var context = new PromptContext
        {
            TaskId = task.Id,
            SessionId = sessionId,
            OriginalPrompt = task.Prompt,
            CompletionPromise = task.CompletionPromise
        };

        _promptBuilder
            .InitializeContextAsync(task.Id, sessionId, task.Prompt, task.CompletionPromise,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(context));

        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Generate code"));

        // First call fails, second succeeds with promise
        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Result.Failure<string>("LLM timeout"),
                Result.Success("Output DONE"));

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

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate code",
            completionPromise: "DONE",
            maxIterations: 100);
        task.Claim(sessionId);

        var context = new PromptContext
        {
            TaskId = task.Id,
            SessionId = sessionId,
            OriginalPrompt = task.Prompt,
            CompletionPromise = task.CompletionPromise
        };

        var cts = new CancellationTokenSource();

        _promptBuilder
            .InitializeContextAsync(task.Id, sessionId, task.Prompt, task.CompletionPromise,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(context));

        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Generate code"));

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                cts.Cancel(); // Cancel after first LLM call
                return Result.Success("Not done yet");
            });

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

        // Act & Assert - cancellation triggers TaskCanceledException from Task.Delay
        Func<Task> act = () => service.ExecuteAsync(task, sessionId, cts.Token);
        await act.Should().ThrowAsync<TaskCanceledException>();

        // Task should NOT have completed all 100 iterations
        task.IterationCount.Should().BeLessThan(100);
    }

    [Fact]
    public async Task ExecuteAsync_CompletionPromiseIsCaseInsensitive()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "COMPLETED",
            maxIterations: 5);
        task.Claim(sessionId);

        var context = new PromptContext
        {
            TaskId = task.Id,
            SessionId = sessionId,
            OriginalPrompt = task.Prompt,
            CompletionPromise = task.CompletionPromise
        };

        _promptBuilder
            .InitializeContextAsync(task.Id, sessionId, task.Prompt, task.CompletionPromise,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(context));

        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Test"));

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("COMPLETED")); // exact case match (ContainsTarget uses Ordinal)

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

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(TaskStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_CallsRecordExecution_ForEachIteration()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "DONE",
            maxIterations: 3);
        task.Claim(sessionId);

        var context = new PromptContext
        {
            TaskId = task.Id,
            SessionId = sessionId,
            OriginalPrompt = task.Prompt,
            CompletionPromise = task.CompletionPromise
        };

        _promptBuilder
            .InitializeContextAsync(task.Id, sessionId, task.Prompt, task.CompletionPromise,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(context));

        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Test"));

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("No promise"));

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

        // Act
        await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert - Should have recorded 3 executions (one per iteration)
        await _taskRepository.Received(3)
            .RecordExecutionAsync(Arg.Any<TaskExecution>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StoresResultOnCompletion()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Generate output",
            completionPromise: "SUCCESS",
            maxIterations: 5);
        task.Claim(sessionId);

        var context = new PromptContext
        {
            TaskId = task.Id,
            SessionId = sessionId,
            OriginalPrompt = task.Prompt,
            CompletionPromise = task.CompletionPromise
        };

        _promptBuilder
            .InitializeContextAsync(task.Id, sessionId, task.Prompt, task.CompletionPromise,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(context));

        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Generate output"));

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Final output with SUCCESS"));

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

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        task.Result.Should().Be("Final output with SUCCESS");
    }

    [Fact]
    public async Task ExecuteAsync_FailsAfterMaxConsecutiveFailures()
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

        var context = new PromptContext
        {
            TaskId = task.Id,
            SessionId = sessionId,
            OriginalPrompt = task.Prompt,
            CompletionPromise = task.CompletionPromise
        };

        _promptBuilder
            .InitializeContextAsync(task.Id, sessionId, task.Prompt, task.CompletionPromise,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(context));

        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Test"));

        // All LLM calls fail
        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("LLM error"));

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

        // Act
        var result = await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert - should stop before 100 iterations
        task.IterationCount.Should().BeLessThanOrEqualTo(3); // max consecutive failures = 2
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesTaskAfterEachIteration()
    {
        // Arrange
        var service = CreateService();
        var sessionId = Guid.NewGuid();
        var task = ApplicationTestFactory.CreateTask(
            prompt: "Test",
            completionPromise: "DONE",
            maxIterations: 2);
        task.Claim(sessionId);

        var context = new PromptContext
        {
            TaskId = task.Id,
            SessionId = sessionId,
            OriginalPrompt = task.Prompt,
            CompletionPromise = task.CompletionPromise
        };

        _promptBuilder
            .InitializeContextAsync(task.Id, sessionId, task.Prompt, task.CompletionPromise,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(context));

        _promptBuilder
            .BuildIterationPromptAsync(Arg.Any<PromptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Test"));

        _llmService
            .InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("No promise here"));

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

        // Act
        await service.ExecuteAsync(task, sessionId, CancellationToken.None);

        // Assert - UpdateAsync called for each iteration
        await _taskRepository.Received(2)
            .UpdateAsync(Arg.Any<DomainTask>(), Arg.Any<CancellationToken>());
    }
}
