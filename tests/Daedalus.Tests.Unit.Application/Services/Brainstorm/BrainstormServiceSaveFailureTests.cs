using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services.Brainstorm;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Services.Brainstorm;

/// <summary>
///     A session the repository fails to save is a failure of the operation, with the repository's reason, never a
///     success the next read contradicts. Phase 2.14: sent messages were lost while the API answered 200.
/// </summary>
public class BrainstormServiceSaveFailureTests
{
    private const string SaveRefusal = "The brainstorm session was modified by another operation. Please retry.";

    private readonly IBrainstormRepository _repository = Substitute.For<IBrainstormRepository>();
    private readonly IAgentFactory _agentFactory = Substitute.For<IAgentFactory>();
    private readonly IProjectRepository _projectRepository = Substitute.For<IProjectRepository>();
    private readonly IPrdService _prdService = Substitute.For<IPrdService>();
    private readonly BrainstormService _service;

    public BrainstormServiceSaveFailureTests()
    {
        _service = new BrainstormService(
            _repository, _agentFactory, _projectRepository, _prdService, Substitute.For<ILogger<BrainstormService>>());
        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>()).Returns(Result.Failure(SaveRefusal));
        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmInvocationResult>.Success(new LlmInvocationResult { Response = "A reply." }));
    }

    private BrainstormSession StoredSession(BrainstormSession? session = null)
    {
        session ??= BrainstormSession.Create(Guid.NewGuid()).Value;
        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(Result<BrainstormSession>.Success(session));
        return session;
    }

    /// <summary>Red: <c>CreateSessionAsync</c> ignores the result of <c>UpdateAsync</c>; the create succeeds.</summary>
    [Fact]
    public async Task A_new_session_that_is_not_saved_fails_with_the_reason()
    {
        var project = ApplicationTestFactory.CreateProject(Guid.NewGuid());
        _projectRepository.GetByIdAsync(project.Id, Arg.Any<CancellationToken>()).Returns(Result<Project>.Success(project));
        _repository.AddAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(call => Result<BrainstormSession>.Success(call.Arg<BrainstormSession>()));

        var result = await _service.CreateSessionAsync(project.Id, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaveRefusal);
    }

    /// <summary>Red: <c>SendMessageAsync</c> ignores the result of <c>UpdateAsync</c>; the send succeeds with the reply.</summary>
    [Fact]
    public async Task A_sent_message_that_is_not_saved_fails_with_the_reason()
    {
        var session = StoredSession();

        var result = await _service.SendMessageAsync(session.Id, "Shop owners.", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaveRefusal);
    }

    /// <summary>Red: <c>AdvancePhaseAsync</c> ignores the result of <c>UpdateAsync</c>; the advance succeeds.</summary>
    [Fact]
    public async Task An_advance_that_is_not_saved_fails_with_the_reason()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]").IsSuccess.Should().BeTrue();
        StoredSession(session);

        var result = await _service.AdvancePhaseAsync(session.Id, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaveRefusal);
    }

    /// <summary>Red: <c>AbandonSessionAsync</c> ignores the result of <c>UpdateAsync</c>; the abandon succeeds.</summary>
    [Fact]
    public async Task An_abandon_that_is_not_saved_fails_with_the_reason()
    {
        var session = StoredSession();

        var result = await _service.AbandonSessionAsync(session.Id, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaveRefusal);
    }

    /// <summary>
    ///     The tasks exist by then, so the failure says they were created and that the session was not saved.
    ///     Red: <c>GenerateTasksAsync</c> ignores the result of <c>UpdateAsync</c>; the generate succeeds.
    ///     Red: the failure returns <c>saved.Error</c> alone; it does not say the tasks were created.
    /// </summary>
    [Fact]
    public async Task Generated_tasks_whose_session_is_not_saved_fail_and_say_the_tasks_exist()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        for (var i = 0; i < 5; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");
            session.AdvancePhase();
        }

        session.SetImplementationPlan("### Task 1: Create entity");
        StoredSession(session);
        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmInvocationResult>.Success(new LlmInvocationResult
            {
                Response = """[{"title":"Create entity","description":"d","acceptanceCriteria":"a","priority":"High","complexity":"Low","dependencies":[],"phase":"P1","parallelGroup":1}]""",
            }));
        _prdService.ConvertToTasksAsync(session.ProjectId, Arg.Any<IReadOnlyList<PrdItemForConversionDto>>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<TaskDto>>.Success([null!, null!]));

        var result = await _service.GenerateTasksAsync(session.Id, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Created 2 tasks, but the session could not be saved: {SaveRefusal}");
    }
}
