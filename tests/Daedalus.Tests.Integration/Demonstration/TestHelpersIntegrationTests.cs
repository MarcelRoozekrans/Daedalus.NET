using System.Diagnostics.CodeAnalysis;
using Daedalus.Tests.Integration.Builders;
using Daedalus.Tests.Integration.Fixtures;

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
            .WithPrompt("Test prompt");

        // Act - Build uses MustSucceed internally
        var task = taskBuilder.Build();

        // Assert
        Assert.NotNull(task);
        Assert.Equal("Test prompt", task.Prompt);
        Assert.Empty(task.CompletionPromise);
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
