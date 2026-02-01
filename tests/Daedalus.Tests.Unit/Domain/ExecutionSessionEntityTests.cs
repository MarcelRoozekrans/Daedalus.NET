using Daedalus.Domain.Entities;
using Daedalus.Tests.Unit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Domain;

/// <summary>
///     Unit tests for ExecutionSession aggregate root.
/// </summary>
public class ExecutionSessionEntityTests : UnitTestBase
{
    private const string _workerName = "worker-001";
    private readonly Guid _sessionId = Guid.NewGuid();

    #region ExecutionSession Task Completion Tests

    [Fact]
    public void RecordTaskCompleted_ShouldIncrementCompletionCount()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        session.TasksCompleted.Should().Be(0);

        // Act & Assert
        session.RecordTaskCompleted();
        session.TasksCompleted.Should().Be(1);

        session.RecordTaskCompleted();
        session.RecordTaskCompleted();
        session.RecordTaskCompleted();
        session.RecordTaskCompleted();
        session.TasksCompleted.Should().Be(5);
    }

    #endregion

    #region ExecutionSession.Create Tests

    [Fact]
    public void Create_WithValidParameters_ShouldSucceed()
    {
        // Act
        var result = ExecutionSession.Create(_sessionId, _workerName);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(_sessionId);
        result.Value.WorkerName.Should().Be(_workerName);
        result.Value.IsActive.Should().BeTrue();
        result.Value.TasksCompleted.Should().Be(0);
        result.Value.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.Value.LastHeartbeat.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Create_WithEmptyWorkerName_ShouldFail()
    {
        // Act
        var result = ExecutionSession.Create(_sessionId, string.Empty);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Worker name cannot be empty");
    }

    [Fact]
    public void Create_WithWhitespaceWorkerName_ShouldFail()
    {
        // Act
        var result = ExecutionSession.Create(_sessionId, "   ");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Worker name cannot be empty");
    }

    [Fact]
    public void Create_WithNullWorkerName_ShouldFail()
    {
        // Act
        var result = ExecutionSession.Create(_sessionId, null!);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Worker name cannot be empty");
    }

    [Fact]
    public void Create_TrimsWorkerName()
    {
        // Arrange
        const string workerNameWithWhitespace = "  worker-002  ";

        // Act
        var result = ExecutionSession.Create(_sessionId, workerNameWithWhitespace);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.WorkerName.Should().Be("worker-002");
    }

    [Fact]
    public void Create_WithVariousWorkerNames_ShouldSucceed()
    {
        // Act & Assert
        var result1 = ExecutionSession.Create(Guid.NewGuid(), "worker-1");
        result1.IsSuccess.Should().BeTrue();

        var result2 = ExecutionSession.Create(Guid.NewGuid(), "my-processor");
        result2.IsSuccess.Should().BeTrue();

        var result3 = ExecutionSession.Create(Guid.NewGuid(), "gpu-compute-01");
        result3.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region ExecutionSession.Heartbeat Tests

    [Fact]
    public async Task Heartbeat_ShouldUpdateLastHeartbeatTimestamp()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        var oldHeartbeat = session.LastHeartbeat;
        await Task.Delay(100);

        // Act
        session.Heartbeat();

        // Assert
        session.LastHeartbeat.Should().BeAfter(oldHeartbeat);
        session.LastHeartbeat.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Heartbeat_MultipleCallsInSequence_ShouldAdvanceTimestamp()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        var heartbeat1 = session.LastHeartbeat;
        await Task.Delay(50);

        // Act
        session.Heartbeat();
        var heartbeat2 = session.LastHeartbeat;
        await Task.Delay(50);
        session.Heartbeat();
        var heartbeat3 = session.LastHeartbeat;

        // Assert
        heartbeat2.Should().BeAfter(heartbeat1);
        heartbeat3.Should().BeAfter(heartbeat2);
    }

    #endregion

    #region ExecutionSession.Shutdown Tests

    [Fact]
    public void Shutdown_ShouldMarkSessionAsInactive()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        session.IsActive.Should().BeTrue();

        // Act
        session.Shutdown();

        // Assert
        session.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Shutdown_MultipleTimes_ShouldRemainInactive()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;

        // Act
        session.Shutdown();
        session.Shutdown();
        session.Shutdown();

        // Assert
        session.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Shutdown_ShouldNotClearLastHeartbeat()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        var heartbeatBefore = session.LastHeartbeat;

        // Act
        session.Shutdown();

        // Assert
        session.LastHeartbeat.Should().Be(heartbeatBefore);
    }

    #endregion

    #region ExecutionSession.IsStale Tests

    [Fact]
    public void IsStale_WithRecentHeartbeat_ShouldReturnFalse()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        var staleness = TimeSpan.FromMinutes(5);

        // Act
        var isStale = session.IsStale(staleness);

        // Assert
        isStale.Should().BeFalse();
    }

    [Fact]
    public void IsStale_WithOldHeartbeat_ShouldReturnTrue()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        session.Heartbeat(DateTime.UtcNow.AddMinutes(-10));
        var staleness = TimeSpan.FromMinutes(5);

        // Act
        var isStale = session.IsStale(staleness);

        // Assert
        isStale.Should().BeTrue();
    }

    [Fact]
    public void IsStale_WithHeartbeatExactlyAtThreshold_ShouldReturnFalse()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        var baseTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var staleness = TimeSpan.FromSeconds(300);

        // Set heartbeat to slightly LESS than staleness ago (to ensure not stale)
        session.Heartbeat(baseTime.AddSeconds(-299.5));

        // Act
        var isStale = session.IsStale(staleness, baseTime);

        // Assert - 299.5 < 300, so not stale
        isStale.Should().BeFalse();
    }

    [Fact]
    public void IsStale_WithHeartbeatJustBeyondThreshold_ShouldReturnTrue()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        session.Heartbeat(DateTime.UtcNow.AddSeconds(-301));
        var staleness = TimeSpan.FromSeconds(300);

        // Act
        var isStale = session.IsStale(staleness);

        // Assert
        isStale.Should().BeTrue();
    }

    [Fact]
    public void IsStale_AfterHeartbeat_ShouldReturnFalse()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        session.Heartbeat(DateTime.UtcNow.AddMinutes(-10));
        var staleness = TimeSpan.FromMinutes(5);
        session.IsStale(staleness).Should().BeTrue();

        // Act
        session.Heartbeat();
        var isStale = session.IsStale(staleness);

        // Assert
        isStale.Should().BeFalse();
    }

    [Fact]
    public void IsStale_WithVariousStalenessThresholds()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        session.Heartbeat(DateTime.UtcNow.AddSeconds(-30));

        // Act & Assert
        session.IsStale(TimeSpan.FromSeconds(10)).Should().BeTrue();
        session.IsStale(TimeSpan.FromSeconds(60)).Should().BeFalse();
        session.IsStale(TimeSpan.FromMinutes(1)).Should().BeFalse();
        session.IsStale(TimeSpan.FromSeconds(25)).Should().BeTrue();
    }

    #endregion

    #region ExecutionSession State Combinations Tests

    [Fact]
    public void Session_CanBeActiveAndStale()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        session.Heartbeat(DateTime.UtcNow.AddMinutes(-10));

        // Act & Assert
        session.IsActive.Should().BeTrue();
        session.IsStale(TimeSpan.FromMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void Session_CanBeInactiveAndNotStale()
    {
        // Arrange
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        session.Shutdown();

        // Act & Assert
        session.IsActive.Should().BeFalse();
        session.IsStale(TimeSpan.FromMinutes(5)).Should().BeFalse();
    }

    [Fact]
    public void Session_Lifecycle_ActiveToInactive()
    {
        // Arrange & Act
        var session = ExecutionSession.Create(_sessionId, _workerName).Value;
        session.IsActive.Should().BeTrue();

        session.Heartbeat();
        session.IsStale(TimeSpan.FromMinutes(5)).Should().BeFalse();

        session.Heartbeat(DateTime.UtcNow.AddMinutes(-10));
        session.IsStale(TimeSpan.FromMinutes(5)).Should().BeTrue();

        session.Shutdown();
        session.IsActive.Should().BeFalse();

        // Assert
        session.WorkerName.Should().Be(_workerName);
        session.Id.Should().Be(_sessionId);
    }

    #endregion

    #region ExecutionSession Properties Tests

    [Fact]
    public void StartedAt_ShouldBeSetOnCreation()
    {
        // Act
        var result = ExecutionSession.Create(_sessionId, _workerName);

        // Assert
        result.Value.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.Value.StartedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void LastHeartbeat_ShouldBeSetOnCreation()
    {
        // Act
        var result = ExecutionSession.Create(_sessionId, _workerName);

        // Assert
        result.Value.LastHeartbeat.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.Value.LastHeartbeat.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Id_ShouldMatchCreationId()
    {
        // Act
        var result = ExecutionSession.Create(_sessionId, _workerName);

        // Assert
        result.Value.Id.Should().Be(_sessionId);
    }

    #endregion
}
