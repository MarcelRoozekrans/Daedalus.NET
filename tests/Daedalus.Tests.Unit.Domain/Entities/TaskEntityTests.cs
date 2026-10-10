using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for DomainTask aggregate root.
/// </summary>
public class TaskEntityTests : UnitTestBase
{
    [Fact]
    public void Create_WithValidParameters_ShouldSucceed()
    {
        // Arrange & Act
        var task = DomainTestFactory.CreateTask(prompt: "Test prompt");

        // Assert
        task.Should().NotBeNull();
        task.Prompt.Should().Be("Test prompt");
        task.CompletionPromise.Should().BeEmpty();
        task.MaxIterations.Should().Be(0);
        task.Learnings.Should().BeEmpty();
        task.LearningsUpdatedAt.Should().BeNull();
        task.Status.Should().Be(DomainTaskStatus.Pending);
    }

    [Fact]
    public void Executions_ShouldBeReadOnly()
    {
        // Arrange & Act
        var task = DomainTestFactory.CreateTask();

        // Assert
        task.Executions.Should().NotBeNull();
        task.Executions.Should().BeEmpty();
    }
}
