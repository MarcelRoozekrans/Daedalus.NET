using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Application.Services.Middleware;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Services;

/// <summary>
///     Unit tests for CodeChangeApplicationMiddleware.
///     Verifies code extraction, application, skip conditions, and non-fatal error handling.
/// </summary>
public class CodeChangeApplicationMiddlewareTests : UnitTestBase
{
    private readonly IGitChangeApplier _changeApplier;
    private readonly ILogger<CodeChangeApplicationMiddleware> _logger;
    private readonly CodeChangeApplicationMiddleware _middleware;

    public CodeChangeApplicationMiddlewareTests()
    {
        _changeApplier = Substitute.For<IGitChangeApplier>();
        _logger = Substitute.For<ILogger<CodeChangeApplicationMiddleware>>();
        _middleware = new CodeChangeApplicationMiddleware(_changeApplier, _logger);
    }

    #region Order

    [Fact]
    public void Order_ShouldBe250_AfterLlmInvocationBeforeCompletionDetection()
    {
        _middleware.Order.Should().Be(250);
    }

    #endregion

    #region Skip Conditions

    [Fact]
    public async Task InvokeAsync_WhenLlmInvocationFailed_ShouldSkipAndCallContinuation()
    {
        // Arrange
        var context = CreateContext(llmSucceeded: false, workspacePath: "/workspace");
        var continuationCalled = false;

        // Act
        await _middleware.InvokeAsync(context, () =>
        {
            continuationCalled = true;
            return Task.FromResult(Result.Success());
        }, _cancellationToken);

        // Assert
        continuationCalled.Should().BeTrue();
        await _changeApplier.DidNotReceive()
            .ExtractChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WhenNoWorkspacePath_ShouldSkipAndCallContinuation()
    {
        // Arrange
        var context = CreateContext(llmSucceeded: true, workspacePath: "");
        var continuationCalled = false;

        // Act
        await _middleware.InvokeAsync(context, () =>
        {
            continuationCalled = true;
            return Task.FromResult(Result.Success());
        }, _cancellationToken);

        // Assert
        continuationCalled.Should().BeTrue();
        await _changeApplier.DidNotReceive()
            .ExtractChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WhenLlmResponseEmpty_ShouldSkipAndCallContinuation()
    {
        // Arrange
        var context = CreateContext(llmSucceeded: true, workspacePath: "/workspace", llmResponse: "");
        var continuationCalled = false;

        // Act
        await _middleware.InvokeAsync(context, () =>
        {
            continuationCalled = true;
            return Task.FromResult(Result.Success());
        }, _cancellationToken);

        // Assert
        continuationCalled.Should().BeTrue();
        await _changeApplier.DidNotReceive()
            .ExtractChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WhenLlmResponseNull_ShouldSkipAndCallContinuation()
    {
        // Arrange
        var context = CreateContext(llmSucceeded: true, workspacePath: "/workspace", llmResponse: null);
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

    #endregion

    #region Successful Extraction and Application

    [Fact]
    public async Task InvokeAsync_WithValidChanges_ShouldExtractAndApply()
    {
        // Arrange
        var context = CreateContext(llmSucceeded: true, workspacePath: "/workspace",
            llmResponse: "```csharp:src/File.cs\nconsole.log('hi');\n```");
        var changes = new List<CodeModification>
        {
            new() { FilePath = "src/File.cs", ModifiedCode = "console.log('hi');" }
        };
        _changeApplier.ExtractChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<CodeModification>>(changes.AsReadOnly()));
        _changeApplier.ApplyChangesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<CodeModification>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _middleware.InvokeAsync(context, () => Task.FromResult(Result.Success()),
            _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _changeApplier.Received(1).ExtractChangesAsync(Arg.Any<string>(), _cancellationToken);
        await _changeApplier.Received(1).ApplyChangesAsync("/workspace",
            Arg.Any<IReadOnlyList<CodeModification>>(), _cancellationToken);
    }

    [Fact]
    public async Task InvokeAsync_WithValidChanges_ShouldStoreAppliedPaths()
    {
        // Arrange
        var context = CreateContext(llmSucceeded: true, workspacePath: "/workspace",
            llmResponse: "some response");
        var changes = new List<CodeModification>
        {
            new() { FilePath = "src/File.cs", ModifiedCode = "code" },
            new() { FilePath = "src/Other.cs", ModifiedCode = "more code" }
        };
        _changeApplier.ExtractChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<CodeModification>>(changes.AsReadOnly()));
        _changeApplier.ApplyChangesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<CodeModification>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        await _middleware.InvokeAsync(context, () => Task.FromResult(Result.Success()), _cancellationToken);

        // Assert
        context.Items.Should().ContainKey("applied_file_paths");
        var paths = context.Items["applied_file_paths"] as List<string>;
        paths.Should().Contain("src/File.cs");
        paths.Should().Contain("src/Other.cs");
    }

    #endregion

    #region No Changes Extracted

    [Fact]
    public async Task InvokeAsync_WhenNoChangesExtracted_ShouldContinuePipeline()
    {
        // Arrange
        var context = CreateContext(llmSucceeded: true, workspacePath: "/workspace",
            llmResponse: "No code blocks here");
        _changeApplier.ExtractChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<CodeModification>>(
                new List<CodeModification>().AsReadOnly()));
        var continuationCalled = false;

        // Act
        await _middleware.InvokeAsync(context, () =>
        {
            continuationCalled = true;
            return Task.FromResult(Result.Success());
        }, _cancellationToken);

        // Assert
        continuationCalled.Should().BeTrue();
        await _changeApplier.DidNotReceive()
            .ApplyChangesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<CodeModification>>(),
                Arg.Any<CancellationToken>());
    }

    #endregion

    #region Non-Fatal Error Handling

    [Fact]
    public async Task InvokeAsync_WhenExtractionFails_ShouldContinuePipeline()
    {
        // Arrange
        var context = CreateContext(llmSucceeded: true, workspacePath: "/workspace",
            llmResponse: "some response");
        _changeApplier.ExtractChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<CodeModification>>("Extraction error"));
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
    public async Task InvokeAsync_WhenApplyFails_ShouldContinuePipeline()
    {
        // Arrange
        var context = CreateContext(llmSucceeded: true, workspacePath: "/workspace",
            llmResponse: "some response");
        var changes = new List<CodeModification>
        {
            new() { FilePath = "src/File.cs", ModifiedCode = "code" }
        };
        _changeApplier.ExtractChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<CodeModification>>(changes.AsReadOnly()));
        _changeApplier.ApplyChangesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<CodeModification>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Apply error"));
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
    public async Task InvokeAsync_WhenExceptionThrown_ShouldContinuePipeline()
    {
        // Arrange
        var context = CreateContext(llmSucceeded: true, workspacePath: "/workspace",
            llmResponse: "some response");
        _changeApplier.ExtractChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Result<IReadOnlyList<CodeModification>>>(_ =>
                throw new InvalidOperationException("Unexpected error"));
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

    #region Continuation Behavior

    [Fact]
    public async Task InvokeAsync_ShouldReturnContinuationResult()
    {
        // Arrange
        var context = CreateContext(llmSucceeded: true, workspacePath: "/workspace",
            llmResponse: "some response");
        var changes = new List<CodeModification>
        {
            new() { FilePath = "src/File.cs", ModifiedCode = "code" }
        };
        _changeApplier.ExtractChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<CodeModification>>(changes.AsReadOnly()));
        _changeApplier.ApplyChangesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<CodeModification>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());

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

    #region Helpers

    private static RalphIterationContext CreateContext(
        bool llmSucceeded = true,
        string workspacePath = "",
        string? llmResponse = "LLM response",
        int iteration = 1)
    {
        var taskResult = DomainTask.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
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
            Configuration = new RalphLoopConfiguration { WorkspacePath = workspacePath },
            LlmInvocationSucceeded = llmSucceeded,
            LlmResponse = llmResponse
        };
    }

    #endregion
}
