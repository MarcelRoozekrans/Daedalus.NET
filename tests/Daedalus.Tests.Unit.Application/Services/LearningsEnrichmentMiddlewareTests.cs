using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Services.Middleware;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>
///     Unit tests for LearningsEnrichmentMiddleware.
///     Verifies enrichment injection behavior, iteration gating, and non-fatal error handling.
/// </summary>
public class LearningsEnrichmentMiddlewareTests : UnitTestBase
{
    private readonly ILearningsService _learningsService;
    private readonly ILogger<LearningsEnrichmentMiddleware> _logger;
    private readonly LearningsEnrichmentMiddleware _middleware;

    public LearningsEnrichmentMiddlewareTests()
    {
        _learningsService = Substitute.For<ILearningsService>();
        _logger = Substitute.For<ILogger<LearningsEnrichmentMiddleware>>();
        _middleware = new LearningsEnrichmentMiddleware(_learningsService, _logger);
    }

    #region Order

    [Fact]
    public void Order_ShouldBe90_ToRunBeforePromptBuilding()
    {
        // Assert
        _middleware.Order.Should().Be(90);
    }

    #endregion

    #region Helpers

    private static RalphIterationContext CreateContext(int iteration, Guid? projectId = null)
    {
        var taskResult = DomainTask.Create(
            Guid.NewGuid(),
            projectId ?? Guid.NewGuid(),
            "TASK-001",
            "Test Task",
            "A test task for middleware testing",
            Priority.Medium,
            "Backend",
            1,
            Complexity.Medium,
            "Implement the feature",
            "TASK_COMPLETE",
            100);

        return new RalphIterationContext
        {
            Task = taskResult.Value,
            SessionId = Guid.NewGuid(),
            PromptContext = new PromptContext
            {
                TaskId = taskResult.Value.Id,
                SessionId = Guid.NewGuid(),
                Iteration = iteration,
                OriginalPrompt = "Implement the feature",
                CompletionPromise = "TASK_COMPLETE"
            },
            Iteration = iteration,
            Configuration = new RalphLoopConfiguration()
        };
    }

    #endregion

    #region Iteration Gating

    [Fact]
    public async Task InvokeAsync_OnIteration1_ShouldEnrichContext()
    {
        // Arrange
        var context = CreateContext(1);
        var continuationCalled = false;
        _learningsService.GetEnrichmentContextAsync(
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("=== CROSS-TASK LEARNINGS ===\nSome learnings"));

        // Act
        await _middleware.InvokeAsync(context, () =>
        {
            continuationCalled = true;
            return Task.FromResult(Result.Success());
        }, _cancellationToken);

        // Assert
        continuationCalled.Should().BeTrue();
        context.PromptContext.AccumulatedLearnings.Should().Contain("CROSS-TASK LEARNINGS");
    }

    [Fact]
    public async Task InvokeAsync_OnIteration2_ShouldSkipEnrichment()
    {
        // Arrange
        var context = CreateContext(2);
        var continuationCalled = false;

        // Act
        await _middleware.InvokeAsync(context, () =>
        {
            continuationCalled = true;
            return Task.FromResult(Result.Success());
        }, _cancellationToken);

        // Assert
        continuationCalled.Should().BeTrue();
        context.PromptContext.AccumulatedLearnings.Should().BeNull();
        await _learningsService.DidNotReceive().GetEnrichmentContextAsync(
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_OnIteration3_ShouldEnrichContext()
    {
        // Arrange
        var context = CreateContext(3);
        _learningsService.GetEnrichmentContextAsync(
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Some enrichment data"));

        // Act
        await _middleware.InvokeAsync(context, () => Task.FromResult(Result.Success()), _cancellationToken);

        // Assert
        context.PromptContext.AccumulatedLearnings.Should().Contain("Some enrichment data");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task InvokeAsync_OnNonEnrichmentIteration_ShouldSkip(int iteration)
    {
        // Arrange
        var context = CreateContext(iteration);

        // Act
        await _middleware.InvokeAsync(context, () => Task.FromResult(Result.Success()), _cancellationToken);

        // Assert
        context.PromptContext.AccumulatedLearnings.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_OnIteration6_ShouldEnrichContext()
    {
        // Arrange — 6 % 3 == 0, so should enrich
        var context = CreateContext(6);
        _learningsService.GetEnrichmentContextAsync(
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Enrichment at iteration 6"));

        // Act
        await _middleware.InvokeAsync(context, () => Task.FromResult(Result.Success()), _cancellationToken);

        // Assert
        context.PromptContext.AccumulatedLearnings.Should().Contain("Enrichment at iteration 6");
    }

    #endregion

    #region Enrichment Behavior

    [Fact]
    public async Task InvokeAsync_WhenEmptyEnrichment_ShouldNotModifyLearnings()
    {
        // Arrange
        var context = CreateContext(1);
        _learningsService.GetEnrichmentContextAsync(
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(string.Empty));

        // Act
        await _middleware.InvokeAsync(context, () => Task.FromResult(Result.Success()), _cancellationToken);

        // Assert
        context.PromptContext.AccumulatedLearnings.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_WithExistingLearnings_ShouldAppend()
    {
        // Arrange
        var context = CreateContext(1);
        context.PromptContext.AccumulatedLearnings = "Existing learnings from iteration";
        _learningsService.GetEnrichmentContextAsync(
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("New cross-task learnings"));

        // Act
        await _middleware.InvokeAsync(context, () => Task.FromResult(Result.Success()), _cancellationToken);

        // Assert
        context.PromptContext.AccumulatedLearnings.Should().Contain("Existing learnings from iteration");
        context.PromptContext.AccumulatedLearnings.Should().Contain("New cross-task learnings");
    }

    [Fact]
    public async Task InvokeAsync_WithEmptyProjectId_ShouldPassNullProjectId()
    {
        // Arrange
        var context = CreateContext(1, Guid.Empty);
        _learningsService.GetEnrichmentContextAsync(
                Arg.Any<string>(), Arg.Is<Guid?>(p => p == null), Arg.Any<Guid>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Enrichment"));

        // Act
        await _middleware.InvokeAsync(context, () => Task.FromResult(Result.Success()), _cancellationToken);

        // Assert
        await _learningsService.Received(1).GetEnrichmentContextAsync(
            Arg.Any<string>(), Arg.Is<Guid?>(p => p == null), Arg.Any<Guid>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Error Handling (Non-Fatal)

    [Fact]
    public async Task InvokeAsync_WhenServiceReturnsFailure_ShouldContinuePipeline()
    {
        // Arrange
        var context = CreateContext(1);
        _learningsService.GetEnrichmentContextAsync(
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("Database connection failed"));
        var continuationCalled = false;

        // Act
        var result = await _middleware.InvokeAsync(context, () =>
        {
            continuationCalled = true;
            return Task.FromResult(Result.Success());
        }, _cancellationToken);

        // Assert
        continuationCalled.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_WhenServiceThrowsException_ShouldContinuePipeline()
    {
        // Arrange
        var context = CreateContext(1);
        _learningsService.GetEnrichmentContextAsync(
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Result<string>>(_ => throw new InvalidOperationException("Unexpected error"));
        var continuationCalled = false;

        // Act
        var result = await _middleware.InvokeAsync(context, () =>
        {
            continuationCalled = true;
            return Task.FromResult(Result.Success());
        }, _cancellationToken);

        // Assert
        continuationCalled.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region Continuation

    [Fact]
    public async Task InvokeAsync_ShouldAlwaysCallContinuation()
    {
        // Arrange
        var context = CreateContext(1);
        _learningsService.GetEnrichmentContextAsync(
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Some enrichment"));
        var continuationCalled = false;

        // Act
        await _middleware.InvokeAsync(context, () =>
        {
            continuationCalled = true;
            return Task.FromResult(Result.Success());
        }, _cancellationToken);

        // Assert
        continuationCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturnContinuationResult()
    {
        // Arrange
        var context = CreateContext(1);
        _learningsService.GetEnrichmentContextAsync(
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<Guid>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Enrichment"));

        // Act
        var result = await _middleware.InvokeAsync(
            context,
            () => Task.FromResult(Result.Failure("Downstream failure")),
            _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Downstream failure");
    }

    #endregion
}
