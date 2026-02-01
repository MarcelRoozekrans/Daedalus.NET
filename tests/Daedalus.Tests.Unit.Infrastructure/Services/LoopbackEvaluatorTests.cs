using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Unit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Infrastructure.Services;

/// <summary>
///     Integration tests for <see cref="LoopbackEvaluator" />.
///     Tests command execution, output parsing logic, and the full evaluate pipeline.
/// </summary>
public class LoopbackEvaluatorTests : UnitTestBase, IDisposable
{
    private readonly LoopbackEvaluator _sut;
    private readonly string _tempDir;

    public LoopbackEvaluatorTests()
    {
        var logger = Substitute.For<ILogger<LoopbackEvaluator>>();
        _sut = new LoopbackEvaluator(logger);
        _tempDir = Path.Combine(Path.GetTempPath(), $"daedalus_loopback_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }

        GC.SuppressFinalize(this);
    }

    #region RunCommandAsync

    [Fact]
    public async Task RunCommandAsync_WithValidCommand_ShouldCaptureOutput()
    {
        // Act — run a simple command (works on both Windows and Linux)
        var result = await _sut.RunCommandAsync(
            _tempDir, "dotnet", "--version", 30, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Succeeded.Should().BeTrue();
        result.Value.StandardOutput.Should().NotBeNullOrEmpty();
        result.Value.ExitCode.Should().Be(0);
        result.Value.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task RunCommandAsync_WithNonExistentCommand_ShouldReturnFailure()
    {
        // Act
        var result = await _sut.RunCommandAsync(
            _tempDir, "totally_nonexistent_command_xyz", "", 5, _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Failed to execute");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunCommandAsync_WithEmptyWorkspacePath_ShouldReturnFailure(string? path)
    {
        // Act
        var result = await _sut.RunCommandAsync(
            path!, "dotnet", "--version", 30, _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Workspace path cannot be empty");
    }

    [Fact]
    public async Task RunCommandAsync_ShouldCaptureDuration()
    {
        // Act
        var result = await _sut.RunCommandAsync(
            _tempDir, "dotnet", "--version", 30, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task RunCommandAsync_WithFailingCommand_ShouldCaptureNonZeroExitCode()
    {
        // Act — dotnet build in an empty directory will fail
        var result = await _sut.RunCommandAsync(
            _tempDir, "dotnet", "build", 30, _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue("RunCommandAsync should succeed — the command itself fails");
        result.Value.Succeeded.Should().BeFalse();
        result.Value.ExitCode.Should().NotBe(0);
    }

    #endregion

    #region EvaluateAsync

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EvaluateAsync_WithEmptyWorkspacePath_ShouldReturnFailure(string? path)
    {
        // Act
        var result = await _sut.EvaluateAsync(path!, "some response", _cancellationToken);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Workspace path cannot be empty");
    }

    [Fact]
    public async Task EvaluateAsync_InEmptyDirectory_ShouldReturnBuildFailure()
    {
        // Act — no project in temp dir, so build will fail
        var result = await _sut.EvaluateAsync(_tempDir, "LLM said something", _cancellationToken);

        // Assert
        result.IsSuccess.Should().BeTrue("EvaluateAsync wraps failures in LoopbackResult");
        result.Value.BuildSucceeded.Should().BeFalse();
        result.Value.TestsPassed.Should().BeFalse("tests shouldn't run when build fails");
    }

    #endregion

    #region LoopbackResult.ToPromptSection

    [Fact]
    public void ToPromptSection_WithSuccessfulBuildAndTests_ShouldFormatCorrectly()
    {
        // Arrange
        var result = new LoopbackResult
        {
            BuildSucceeded = true,
            BuildOutput = "Build succeeded.",
            TestsPassed = true,
            TestOutput = "Test run passed.",
            TestsPassed_Count = 42,
            TestsFailed_Count = 0,
            TestsSkipped_Count = 3,
            CompilationErrors = [],
            TestFailures = []
        };

        // Act
        var section = result.ToPromptSection();

        // Assert
        section.Should().Contain("BUILD: SUCCESS");
        section.Should().Contain("TESTS: ALL PASSED");
        section.Should().Contain("42 passed");
        section.Should().Contain("3 skipped");
    }

    [Fact]
    public void ToPromptSection_WithBuildFailure_ShouldIncludeErrors()
    {
        // Arrange
        var result = new LoopbackResult
        {
            BuildSucceeded = false,
            BuildOutput = "error CS1234: Something went wrong",
            TestsPassed = false,
            CompilationErrors = ["error CS1234: Type not found", "error CS5678: Missing reference"],
            TestFailures = []
        };

        // Act
        var section = result.ToPromptSection();

        // Assert
        section.Should().Contain("BUILD: FAILED");
        section.Should().Contain("ERROR: error CS1234");
        section.Should().Contain("ERROR: error CS5678");
    }

    [Fact]
    public void ToPromptSection_WithTestFailures_ShouldIncludeFailureDetails()
    {
        // Arrange
        var result = new LoopbackResult
        {
            BuildSucceeded = true,
            TestsPassed = false,
            TestsPassed_Count = 10,
            TestsFailed_Count = 2,
            TestsSkipped_Count = 1,
            CompilationErrors = [],
            TestFailures = ["Failed TestMethodA: Expected true but was false", "Failed TestMethodB: Timeout"]
        };

        // Act
        var section = result.ToPromptSection();

        // Assert
        section.Should().Contain("TESTS: FAILED");
        section.Should().Contain("10 passed");
        section.Should().Contain("2 failed");
        section.Should().Contain("FAILURE: Failed TestMethodA");
        section.Should().Contain("FAILURE: Failed TestMethodB");
    }

    [Fact]
    public void ToPromptSection_WithManyErrors_ShouldTruncate()
    {
        // Arrange — more than 10 compilation errors
        var errors = Enumerable.Range(1, 15)
            .Select(i => $"error CS{i:D4}: Error number {i}")
            .ToList();

        var result = new LoopbackResult { BuildSucceeded = false, CompilationErrors = errors, TestFailures = [] };

        // Act
        var section = result.ToPromptSection();

        // Assert
        section.Should().Contain("... and 5 more errors");
    }

    [Fact]
    public void ToPromptSection_WithManyTestFailures_ShouldTruncate()
    {
        // Arrange — more than 5 test failures
        var failures = Enumerable.Range(1, 8)
            .Select(i => $"Failed Test{i}: assertion failed")
            .ToList();

        var result = new LoopbackResult
        {
            BuildSucceeded = true, TestsPassed = false, CompilationErrors = [], TestFailures = failures
        };

        // Act
        var section = result.ToPromptSection();

        // Assert
        section.Should().Contain("... and 3 more failures");
    }

    #endregion

    #region CommandExecutionResult

    [Fact]
    public void CommandExecutionResult_Succeeded_ShouldBeTrueForExitCode0()
    {
        var result = new CommandExecutionResult { ExitCode = 0 };
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void CommandExecutionResult_Succeeded_ShouldBeFalseForNonZeroExitCode()
    {
        var result = new CommandExecutionResult { ExitCode = 1 };
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void CommandExecutionResult_TimedOut_ShouldTrackTimeout()
    {
        var result = new CommandExecutionResult { ExitCode = -1, TimedOut = true };
        result.Succeeded.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
    }

    #endregion
}
