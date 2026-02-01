using System.Diagnostics.CodeAnalysis;
using Daedalus.Tests.Integration.Builders;
using Daedalus.Tests.Integration.Fixtures;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Integration.Demonstration;

/// <summary>
///     Demonstrates the new test helpers and builders working correctly.
///     This validates Week 1 infrastructure is functional and ready for use.
/// </summary>
[Collection(DatabaseCollection.Name)]
[SuppressMessage("Style", "CA1707:Remove underscores from member names")]
[SuppressMessage("Usage", "MA0074:Use an overload of 'Contains' that has a StringComparison parameter")]
public class TestHelpersIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public void ResultTestExtensions_MustSucceed_Extracts_Value()
    {
        // Arrange
        var taskBuilder = new TaskTestBuilder()
            .WithPrompt("Test prompt")
            .WithCompletionPromise("Done");

        // Act - Build uses MustSucceed internally
        var task = taskBuilder.Build();

        // Assert
        Assert.NotNull(task);
        Assert.Equal("Test prompt", task.Prompt);
        Assert.Equal("Done", task.CompletionPromise);
    }

    [Fact]
    public void TaskTestBuilder_ClaimedBy_Sets_Status_InProgress()
    {
        // Arrange
        var sessionId = Guid.NewGuid();

        // Act
        var task = new TaskTestBuilder()
            .WithPrompt("Claimed task")
            .ClaimedBy(sessionId)
            .Build();

        // Assert
        Assert.Equal(sessionId, task.CurrentSessionId);
        Assert.True(task.Status == TaskStatus.InProgress);
    }

    [Fact]
    public void ExecutionSessionTestBuilder_AsStale_Creates_Old_Heartbeat()
    {
        // Arrange
        var staleness = TimeSpan.FromMinutes(15);

        // Act
        var session = new ExecutionSessionTestBuilder()
            .WithWorkerName("stale-worker")
            .AsStale(staleness)
            .Build();

        // Assert
        Assert.NotNull(session);
        Assert.Equal("stale-worker", session.WorkerName);
        // Heartbeat should be in the past
        Assert.True(session.LastHeartbeat < DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void PostgresFixture_Provides_Valid_ConnectionString()
    {
        // Act
        var connectionString = fixture.ConnectionString;

        // Assert
        Assert.NotNull(connectionString);
        Assert.NotEmpty(connectionString);
        Assert.Contains("127.0.0.1", connectionString, StringComparison.OrdinalIgnoreCase);
        // Connection string has parameters like Host, Port, Database, etc.
        Assert.Contains("Database", connectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgresFixture_Host_IsNotEmpty()
    {
        // Act
        var host = fixture.Host;

        // Assert
        Assert.NotNull(host);
        Assert.NotEmpty(host);
    }
}
