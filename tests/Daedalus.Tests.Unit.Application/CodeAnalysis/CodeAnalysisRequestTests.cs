using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Tests.Unit.Application.CodeAnalysis;

/// <summary>
///     Unit tests for CodeAnalysisRequest domain entity
/// </summary>
public class CodeAnalysisRequestTests
{
    [Fact]
    public void Constructor_CreatesValidRequest()
    {
        // Arrange & Act
        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Test Analysis",
            "Test description",
            "https://github.com/org/repo",
            10,
            AnalysisType.PerformanceOptimization);
        var request = createResult.Value;

        // Assert
        request.Should().NotBeNull();
        request.Title.Should().Be("Test Analysis");
        request.Status.Should().Be(AnalysisStatus.Pending);
        request.Iterations.Should().BeEmpty();
    }

    [Fact]
    public void AddIteration_AddsIterationToRequest()
    {
        // Arrange
        var createResult = CodeAnalysisRequest.Create(
            Guid.NewGuid(),
            "Test",
            "Test description",
            "https://github.com/org/repo",
            10,
            AnalysisType.CodeReview,
            "src/test.cs");
        var request = createResult.Value;

        // Transition through required states: Pending → Ready → AnalysisInProgress
        request.InitializeRepository("/tmp/worktree", "feature/test");
        request.StartAnalysis();

        var iterationResult = AnalysisIteration.Create(
            Guid.NewGuid(),
            request.Id,
            1,
            "Test prompt",
            "Test response");
        iterationResult.IsSuccess.Should().BeTrue();
        var iteration = iterationResult.Value;

        // Act
        request.RecordIteration(iteration);

        // Assert
        request.Iterations.Should().HaveCount(1);
        request.Iterations[0].IterationNumber.Should().Be(1);
    }
}
