using Daedalus.Api.Controllers;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Controllers;

public class BrainstormControllerTests
{
    private readonly IBrainstormService _service = Substitute.For<IBrainstormService>();
    private readonly ILogger<BrainstormController> _logger = Substitute.For<ILogger<BrainstormController>>();
    private readonly BrainstormController _controller;

    public BrainstormControllerTests()
    {
        _controller = new BrainstormController(_service, _logger);
    }

    [Fact]
    public async Task CreateSession_WithValidProjectId_Returns200()
    {
        var projectId = Guid.NewGuid();
        var session = BrainstormSession.Create(projectId).Value;
        _service.CreateSessionAsync(projectId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        var result = await _controller.CreateSession(
            new Application.DTOs.CreateBrainstormSessionDto(projectId));

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CreateSession_WhenServiceFails_Returns400()
    {
        _service.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BrainstormSession>("Project not found"));

        var result = await _controller.CreateSession(
            new Application.DTOs.CreateBrainstormSessionDto(Guid.NewGuid()));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetSession_WithExistingId_Returns200()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        _service.GetSessionAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        var result = await _controller.GetSession(session.Id);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SendMessage_WithValidContent_Returns200()
    {
        var sessionId = Guid.NewGuid();
        var message = BrainstormMessage.Create(sessionId, MessageRole.Assistant, "Response", BrainstormPhase.Clarification).Value;
        _service.SendMessageAsync(sessionId, "Hello", Arg.Any<CancellationToken>())
            .Returns(Result.Success(message));

        var result = await _controller.SendMessage(
            sessionId,
            new Application.DTOs.SendBrainstormMessageDto("Hello"));

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task AdvancePhase_WhenSignaled_Returns200()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        _service.AdvancePhaseAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        var result = await _controller.AdvancePhase(session.Id);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task AbandonSession_Returns204()
    {
        var sessionId = Guid.NewGuid();
        _service.AbandonSessionAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.AbandonSession(sessionId);

        result.Should().BeOfType<NoContentResult>();
    }
}
