using System.Diagnostics;

namespace Daedalus.Tests.Unit.Domain;

/// <summary>
///     Tests for task learnings functionality.
///     Validates that learnings are properly stored, updated, and tracked.
/// </summary>
public class TaskLearningsTests
{
    [Fact]
    public void Task_InitiallyHasEmptyLearnings()
    {
        // Arrange & Act
        var task = DomainTestFactory.CreateTask(
            Guid.NewGuid(),
            prompt: "Test prompt",
            completionPromise: "SUCCESS",
            maxIterations: 5);

        // Assert
        Assert.Empty(task.Learnings);
        Assert.Null(task.LearningsUpdatedAt);
    }

    [Fact]
    public void UpdateLearnings_WithValidContent_ShouldSucceed()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(
            Guid.NewGuid(),
            prompt: "Test prompt",
            completionPromise: "SUCCESS",
            maxIterations: 5);

        var learningsText = "✓ Success at iteration 3\n⚠ Connection timeout error\nℹ Average response: 512 chars";

        // Act
        var result = task.UpdateLearnings(learningsText);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(learningsText, task.Learnings);
        Assert.NotNull(task.LearningsUpdatedAt);
        Assert.True((DateTime.UtcNow - task.LearningsUpdatedAt.Value).TotalSeconds < 1);
    }

    [Fact]
    public void UpdateLearnings_WithEmptyString_ShouldFail()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(
            Guid.NewGuid(),
            prompt: "Test prompt",
            completionPromise: "SUCCESS",
            maxIterations: 5);

        // Act
        var result = task.UpdateLearnings(string.Empty);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Contains("cannot be empty", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateLearnings_WithWhitespace_ShouldFail()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(
            Guid.NewGuid(),
            prompt: "Test prompt",
            completionPromise: "SUCCESS",
            maxIterations: 5);

        // Act
        var result = task.UpdateLearnings("   ");

        // Assert
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void UpdateLearnings_WithWhitespaceContent_ShouldTrim()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(
            Guid.NewGuid(),
            prompt: "Test prompt",
            completionPromise: "SUCCESS",
            maxIterations: 5);

        var learningsText = "   Some learnings   \n  with extra whitespace  ";

        // Act
        var result = task.UpdateLearnings(learningsText);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("Some learnings   \n  with extra whitespace", task.Learnings);
    }

    [Fact]
    public void UpdateLearnings_MultipleTimes_ShouldReplaceOldContent()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(
            Guid.NewGuid(),
            prompt: "Test prompt",
            completionPromise: "SUCCESS",
            maxIterations: 5);

        var firstLearnings = "First attempt learnings";
        var secondLearnings = "Updated learnings after more executions";

        // Act
        task.UpdateLearnings(firstLearnings);
        var firstUpdate = task.LearningsUpdatedAt;

        // Small delay to allow time to pass
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10)
        {
            // Wait
        }

        sw.Stop();

        var result = task.UpdateLearnings(secondLearnings);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(secondLearnings, task.Learnings);
        Assert.True(task.LearningsUpdatedAt > firstUpdate);
    }

    [Fact]
    public void UpdateLearnings_WithLongContent_ShouldSucceed()
    {
        // Arrange
        var task = DomainTestFactory.CreateTask(
            Guid.NewGuid(),
            prompt: "Test prompt",
            completionPromise: "SUCCESS",
            maxIterations: 5);

        // Create a long learnings string (1000+ chars)
        var learningsText = string.Join(
            '\n',
            Enumerable.Range(1, 50).Select(i => $"Learning {i}: {new string('x', 30)}"));

        // Act
        var result = task.UpdateLearnings(learningsText);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(learningsText, task.Learnings);
        Assert.True(task.Learnings.Length > 1000);
    }
}
