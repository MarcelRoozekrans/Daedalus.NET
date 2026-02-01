using Daedalus.Api.Controllers;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Integration tests for ExecutionSessionsController HTTP endpoints.
///     Tests session data retrieval and status codes.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ExecutionSessionsControllerIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ILogger<ExecutionSessionsController> _loggerMock =
        Substitute.For<ILogger<ExecutionSessionsController>>();

    private ExecutionSessionsController _controller = null!;
    private ApplicationDbContext _dbContext = null!;
    private IExecutionSessionQueryService _sessionQueryService = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _sessionQueryService = new ExecutionSessionQueryService(_dbContext);
        _controller = new ExecutionSessionsController(_sessionQueryService, _loggerMock);
    }

    public async Task DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }

    [Fact(Timeout = 5000)]
    public async Task GetAllSessions_Returns200()
    {
        // Act
        var result = await _controller.GetAllSessions();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        ((OkObjectResult)result).StatusCode.Should().Be(200);
    }

    [Fact(Timeout = 5000)]
    public async Task GetSessionById_WithValidId_Returns200()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _dbContext.ExecutionSessions.Add(ExecutionSession.Create(sessionId, "worker-1").Value);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetSessionById(sessionId);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact(Timeout = 5000)]
    public async Task GetSessionById_WithInvalidId_Returns404()
    {
        // Act
        var result = await _controller.GetSessionById(Guid.NewGuid());

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
        ((NotFoundObjectResult)result).StatusCode.Should().Be(404);
    }

    [Fact(Timeout = 5000)]
    public async Task GetActiveSessions_ReturnsOnlyActive()
    {
        // Arrange
        var activeId = Guid.NewGuid();
        var inactiveId = Guid.NewGuid();
        var active = ExecutionSession.Create(activeId, "worker-1").Value;
        var inactive = ExecutionSession.Create(inactiveId, "worker-2").Value;
        inactive.Shutdown();

        _dbContext.ExecutionSessions.AddRange(active, inactive);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetActiveSessions();

        // Assert
        var okResult = (OkObjectResult)result;
        var pagedResult = okResult.Value as PagedResultDto<ExecutionSessionDto>;
        pagedResult!.Items.Should().HaveCount(1);
        pagedResult.Items[0].IsActive.Should().BeTrue();
    }
}