using System.Diagnostics.CodeAnalysis;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Tests for PostgresFixture to verify testcontainer startup and connection.
/// </summary>
[SuppressMessage("Usage", "MA0074:Use an overload of 'Contains' that has a StringComparison parameter")]
public class PostgresFixtureBasicTests : IAsyncLifetime
{
    private PostgresFixture? _fixture;

    public async Task InitializeAsync()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_fixture != null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [Fact]
    public void ConnectionString_IsNotEmpty()
    {
        var connectionString = _fixture!.ConnectionString;
        Assert.NotNull(connectionString);
        Assert.NotEmpty(connectionString);
        Assert.Contains("127.0.0.1", connectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Host_IsNotEmpty()
    {
        var host = _fixture!.Host;
        Assert.NotNull(host);
        Assert.NotEmpty(host);
    }
}
