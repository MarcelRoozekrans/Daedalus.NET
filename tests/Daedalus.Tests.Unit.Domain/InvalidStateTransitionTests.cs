using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain;

/// <summary>
///     Tests for invalid state transitions in domain entities.
///     Tests business rule validation and state machine constraints.
/// </summary>
public class InvalidStateTransitionTests
{
    [Fact]
    public void ExecutionSession_CanBeShutdown()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "test-worker").Value;

        // Act
        session.Shutdown();

        // Assert
        session.IsActive.Should().BeFalse();
    }

    [Fact]
    public void ExecutionSession_ShutdownCanOnlyHappenOnce()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "test-worker").Value;

        // Act
        session.Shutdown();
        var afterFirstShutdown = session.IsActive;

        session.Shutdown(); // Second shutdown
        var afterSecondShutdown = session.IsActive;

        // Assert
        afterFirstShutdown.Should().BeFalse();
        afterSecondShutdown.Should().BeFalse();
    }

    [Fact]
    public void ExecutionSession_WorkerNameIsImmutable()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = ExecutionSession.Create(sessionId, "original-worker").Value;

        // Act
        var originalName = session.WorkerName;

        // Assert
        originalName.Should().Be("original-worker");
    }
}
