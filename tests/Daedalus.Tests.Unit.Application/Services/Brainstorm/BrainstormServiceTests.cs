using Daedalus.Application.Abstractions;
using Daedalus.Application.Services.Brainstorm;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Services.Brainstorm;

public class BrainstormServiceTests
{
    private readonly IBrainstormRepository _repository = Substitute.For<IBrainstormRepository>();
    private readonly IRalphAgentFactory _agentFactory = Substitute.For<IRalphAgentFactory>();
    private readonly IProjectRepository _projectRepository = Substitute.For<IProjectRepository>();
    private readonly IPrdService _prdService = Substitute.For<IPrdService>();
    private readonly ILogger<BrainstormService> _logger = Substitute.For<ILogger<BrainstormService>>();
    private readonly BrainstormService _service;

    public BrainstormServiceTests()
    {
        _service = new BrainstormService(_repository, _agentFactory, _projectRepository, _prdService, _logger);
    }

    [Fact]
    public async Task CreateSessionAsync_WithValidProject_ShouldCreateAndReturnSession()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = ApplicationTestFactory.CreateProject(projectId);
        _projectRepository.GetByIdAsync(projectId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(project));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LlmInvocationResult
            {
                Response = "Project summary here. [PHASE_COMPLETE]",
                InputTokens = 100,
                OutputTokens = 200,
                ModelId = "claude"
            }));

        _repository.AddAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result.Success(callInfo.Arg<BrainstormSession>()));

        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _service.CreateSessionAsync(projectId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Phase.Should().Be(BrainstormPhase.ContextGathering);
        result.Value.Messages.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateSessionAsync_WithInvalidProject_ShouldReturnFailure()
    {
        // Arrange
        _projectRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Daedalus.Domain.Entities.Project>("Project not found"));

        // Act
        var result = await _service.CreateSessionAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Project");
    }

    [Fact]
    public async Task CreateSessionAsync_WhenLlmFails_ShouldReturnFailure()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = ApplicationTestFactory.CreateProject(projectId);
        _projectRepository.GetByIdAsync(projectId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(project));

        _repository.AddAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result.Success(callInfo.Arg<BrainstormSession>()));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LlmInvocationResult>("LLM service unavailable"));

        // Act
        var result = await _service.CreateSessionAsync(projectId, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("LLM invocation failed");
    }

    [Fact]
    public async Task GetSessionAsync_WithExistingId_ShouldReturnSession()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        // Act
        var result = await _service.GetSessionAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(session.Id);
    }

    [Fact]
    public async Task GetSessionsByProjectAsync_ShouldDelegateToRepository()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var sessions = new List<BrainstormSession>
        {
            BrainstormSession.Create(projectId).Value,
            BrainstormSession.Create(projectId).Value
        };
        _repository.GetByProjectIdAsync(projectId, Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<BrainstormSession>>(sessions));

        // Act
        var result = await _service.GetSessionsByProjectAsync(projectId, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task SendMessageAsync_ShouldCallLlmAndStoreResponse()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        session.AddMessage(MessageRole.Assistant, "Context gathered. [PHASE_COMPLETE]");
        session.AdvancePhase();

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LlmInvocationResult
            {
                Response = "What type of feature?",
                InputTokens = 100,
                OutputTokens = 200,
                ModelId = "claude"
            }));

        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _service.SendMessageAsync(session.Id, "I need a caching layer", CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(MessageRole.Assistant);
        result.Value.Content.Should().Be("What type of feature?");
    }

    [Fact]
    public async Task SendMessageAsync_WhenSessionNotFound_ShouldReturnFailure()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _repository.GetByIdAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BrainstormSession>("Session not found"));

        // Act
        var result = await _service.SendMessageAsync(sessionId, "Hello", CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Session not found");
    }

    [Fact]
    public async Task SendMessageAsync_WhenLlmFails_ShouldReturnFailure()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LlmInvocationResult>("LLM timeout"));

        // Act
        var result = await _service.SendMessageAsync(session.Id, "Hello", CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("LLM invocation failed");
    }

    [Fact]
    public async Task AdvancePhaseAsync_WhenSignaled_ShouldAdvanceAndCallLlm()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LlmInvocationResult
            {
                Response = "First question?",
                InputTokens = 100,
                OutputTokens = 200,
                ModelId = "claude"
            }));

        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _service.AdvancePhaseAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Phase.Should().Be(BrainstormPhase.Clarification);
    }

    [Fact]
    public async Task AdvancePhaseAsync_WhenNotSignaled_ShouldReturnFailure()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        // No [PHASE_COMPLETE] marker -- PhaseCompleteSignaled is false

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        // Act
        var result = await _service.AdvancePhaseAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Phase completion has not been signaled");
    }

    [Fact]
    public async Task AdvancePhaseAsync_WhenSessionNotFound_ShouldReturnFailure()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _repository.GetByIdAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BrainstormSession>("Session not found"));

        // Act
        var result = await _service.AdvancePhaseAsync(sessionId, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task AbandonSessionAsync_ShouldMarkAsAbandoned()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _service.AbandonSessionAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AbandonSessionAsync_WhenSessionNotFound_ShouldReturnFailure()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _repository.GetByIdAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BrainstormSession>("Session not found"));

        // Act
        var result = await _service.AbandonSessionAsync(sessionId, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task AbandonSessionAsync_WhenAlreadyAbandoned_ShouldReturnFailure()
    {
        // Arrange
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        session.Abandon(); // Already abandoned

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        // Act
        var result = await _service.AbandonSessionAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
    }
}
