using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services.Brainstorm;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Services.Brainstorm;

public class BrainstormServiceGenerateTasksTests
{
    private readonly IBrainstormRepository _repository = Substitute.For<IBrainstormRepository>();
    private readonly IRalphAgentFactory _agentFactory = Substitute.For<IRalphAgentFactory>();
    private readonly IProjectRepository _projectRepository = Substitute.For<IProjectRepository>();
    private readonly IPrdService _prdService = Substitute.For<IPrdService>();
    private readonly ILogger<BrainstormService> _logger = Substitute.For<ILogger<BrainstormService>>();
    private readonly BrainstormService _service;

    public BrainstormServiceGenerateTasksTests()
    {
        _service = new BrainstormService(_repository, _agentFactory, _projectRepository, _prdService, _logger);
    }

    private static BrainstormSession CreateSessionInTaskCreationPhase()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        for (var i = 0; i < 5; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");
            session.AdvancePhase();
        }

        // Now session.Phase == BrainstormPhase.TaskCreation
        session.SetImplementationPlan("### Task 1: Create entity\nSome plan content");
        return session;
    }

    private static string GetValidLlmJsonResponse()
    {
        return """
            [
              {
                "title": "Create entity",
                "description": "Create the domain entity",
                "acceptanceCriteria": "Entity exists with correct properties",
                "priority": "High",
                "complexity": "Medium",
                "dependencies": [],
                "phase": "Phase 1",
                "parallelGroup": 1
              }
            ]
            """;
    }

    private static List<TaskDto> CreateSampleTaskDtos()
    {
        return
        [
            new TaskDto(
                Id: Guid.NewGuid(),
                TaskId: "TASK-001",
                ProjectId: Guid.NewGuid(),
                Title: "Create entity",
                Description: "Create the domain entity",
                Priority: 2,
                Phase: "Phase 1",
                ParallelGroup: 1,
                Dependencies: [],
                FilesToModify: [],
                EstimatedComplexity: 1,
                Prompt: "Create entity",
                CompletionPromise: "Entity created",
                MaxIterations: 5,
                Status: 0,
                CurrentSessionId: null,
                Result: null,
                IterationCount: 0,
                CreatedAt: DateTime.UtcNow,
                CompletedAt: null,
                Learnings: null,
                LearningsUpdatedAt: null,
                Executions: [])
        ];
    }

    [Fact]
    public async Task GenerateTasksAsync_InTaskCreationPhase_ShouldCallPrdService()
    {
        // Arrange
        var session = CreateSessionInTaskCreationPhase();

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LlmInvocationResult
            {
                Response = GetValidLlmJsonResponse(),
                InputTokens = 100,
                OutputTokens = 200,
                ModelId = "claude"
            }));

        var expectedTasks = CreateSampleTaskDtos();
        _prdService.ConvertToTasksAsync(
                session.ProjectId,
                Arg.Any<IReadOnlyList<PrdItemForConversionDto>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedTasks));

        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _service.GenerateTasksAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Title.Should().Be("Create entity");

        await _prdService.Received(1).ConvertToTasksAsync(
            session.ProjectId,
            Arg.Any<IReadOnlyList<PrdItemForConversionDto>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateTasksAsync_NotInTaskCreationPhase_ShouldFail()
    {
        // Arrange — session is in ContextGathering phase (default)
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        // Act
        var result = await _service.GenerateTasksAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("TaskCreation phase");
    }

    [Fact]
    public async Task GenerateTasksAsync_SessionNotFound_ShouldFail()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _repository.GetByIdAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BrainstormSession>("Session not found"));

        // Act
        var result = await _service.GenerateTasksAsync(sessionId, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Session not found");
    }

    [Fact]
    public async Task GenerateTasksAsync_NoPlan_ShouldFail()
    {
        // Arrange — session in TaskCreation phase but no implementation plan
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        for (var i = 0; i < 5; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");
            session.AdvancePhase();
        }

        // Do NOT set implementation plan

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        // Act
        var result = await _service.GenerateTasksAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no implementation plan");
    }

    [Fact]
    public async Task GenerateTasksAsync_LlmFailure_ShouldFail()
    {
        // Arrange
        var session = CreateSessionInTaskCreationPhase();

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LlmInvocationResult>("LLM service unavailable"));

        // Act
        var result = await _service.GenerateTasksAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("LLM invocation failed");
    }

    [Fact]
    public async Task GenerateTasksAsync_InvalidJson_ShouldFail()
    {
        // Arrange
        var session = CreateSessionInTaskCreationPhase();

        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(Result.Success(session));

        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LlmInvocationResult
            {
                Response = "This is not valid JSON at all",
                InputTokens = 100,
                OutputTokens = 50,
                ModelId = "claude"
            }));

        // Act
        var result = await _service.GenerateTasksAsync(session.Id, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Failed to parse LLM response");
    }
}
