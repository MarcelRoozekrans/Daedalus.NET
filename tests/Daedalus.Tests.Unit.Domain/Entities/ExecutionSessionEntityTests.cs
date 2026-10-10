using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for ExecutionSession aggregate root.
/// </summary>
public class ExecutionSessionEntityTests : UnitTestBase
{
    private const string _workerName = "worker-001";
    private readonly Guid _sessionId = Guid.NewGuid();

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
