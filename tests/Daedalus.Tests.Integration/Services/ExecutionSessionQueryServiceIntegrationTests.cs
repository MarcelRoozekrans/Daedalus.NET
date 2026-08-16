using Daedalus.Api.Services;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Services;

/// <summary>
///     Integration tests for ExecutionSessionQueryService.
/// </summary>
public class ExecutionSessionQueryServiceIntegrationTests : IAsyncLifetime
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ExecutionSessionQueryService _sut;

    public ExecutionSessionQueryServiceIntegrationTests()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _sut = new ExecutionSessionQueryService(_dbContext);
    }

    public async Task InitializeAsync() => await _dbContext.Database.EnsureCreatedAsync();

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    [Fact(Timeout = 5000)]
    public async Task GetAllAsync_WithSessions_ReturnsAll()
    {
        // Arrange
        var sessionId1 = Guid.NewGuid();
        var sessionId2 = Guid.NewGuid();
        var session1 = ExecutionSession.Create(sessionId1, "worker-1").Value;
        var session2 = ExecutionSession.Create(sessionId2, "worker-2").Value;

        _dbContext.ExecutionSessions.AddRange(session1, session2);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetAllAsync();

        // Assert
        result.Items.Should().HaveCount(2);
        result.Total.Should().Be(2);
    }

    [Fact(Timeout = 5000)]
    public async Task GetActiveAsync_ReturnsOnlyActiveSessions()
    {
        // Arrange
        var sessionId1 = Guid.NewGuid();
        var sessionId2 = Guid.NewGuid();
        var session1 = ExecutionSession.Create(sessionId1, "worker-1").Value;
        var session2 = ExecutionSession.Create(sessionId2, "worker-2").Value;
        session2.Shutdown();

        _dbContext.ExecutionSessions.AddRange(session1, session2);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetActiveAsync();

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].IsActive.Should().BeTrue();
    }
}
