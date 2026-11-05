using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services;
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
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Result<BrainstormSession>.Success(BrainstormSession.Rehydrate(
                call.Arg<Guid>(), project.Id, BrainstormPhase.ContextGathering, null, null, false, DateTime.UtcNow, null,
                1, [])));

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
    ///     The tasks exist by then, so the failure says they were created, and, the refusal being a lost race that
    ///     cannot be redone here (the read back finds the session past TaskCreation), not to generate them again; it
    ///     carries no conflict marker (ruling R83).
    ///     Red: <c>GenerateTasksAsync</c> ignores the result of <c>UpdateAsync</c>; the generate succeeds.
    ///     Red: the failure quotes the refusal, as before; it invites the retry.
    /// </summary>
    [Fact]
    public async Task Generated_tasks_whose_session_is_not_saved_fail_and_say_the_tasks_exist()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        for (var i = 0; i < 5; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]").IsSuccess.Should().BeTrue();
            session.AdvancePhase().IsSuccess.Should().BeTrue();
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
        result.Error.Should().Be(CompletionNotSaved);
    }

    /// <summary>
    ///     Ruling R83: a lost race on the completed session is redone on the session read again while it is still in
    ///     TaskCreation, so the generate succeeds and the tasks are converted once. Red: answer the first refusal; the
    ///     generate fails.
    /// </summary>
    [Fact]
    public async Task Generated_tasks_whose_session_save_loses_once_are_saved_on_the_session_read_again()
    {
        var session = TaskCreationSessionOnEveryRead();
        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(SaveRefusal), Result.Success());

        var result = await _service.GenerateTasksAsync(session.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : string.Empty);
        await _prdService.Received(1).ConvertToTasksAsync(
            session.ProjectId, Arg.Any<IReadOnlyList<PrdItemForConversionDto>>(), Arg.Any<CancellationToken>());
        await _repository.Received(2).UpdateAsync(
            Arg.Is<BrainstormSession>(s => s.Phase == BrainstormPhase.Completed), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Ruling R83: the redo is bounded; after three lost saves the generate gives up with the text that says the tasks
    ///     exist. Red: allow two saves in place of three; the save is attempted twice.
    /// </summary>
    [Fact(Timeout = 5000)]
    public async Task Generated_tasks_whose_session_save_keeps_losing_give_up_after_three_saves()
    {
        var session = TaskCreationSessionOnEveryRead();

        var result = await _service.GenerateTasksAsync(session.Id, CancellationToken.None);

        result.Error.Should().Be(CompletionNotSaved);
        await _repository.Received(3).UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A refusal that is not a lost race is not redone, and answers the same do-not-generate-again text, its reason
    ///     logged (ruling R83). Red: redo every refusal; the save is attempted three times.
    /// </summary>
    [Fact]
    public async Task Generated_tasks_whose_session_save_fails_otherwise_are_not_redone()
    {
        var session = TaskCreationSessionOnEveryRead();
        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Failed to update brainstorm session x. The cause is logged."));

        var result = await _service.GenerateTasksAsync(session.Id, CancellationToken.None);

        result.Error.Should().Be(CompletionNotSaved);
        await _repository.Received(1).UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Ruling R83: once the tasks exist, no failure may invite a retry, which would create them again, so a refusal
    ///     whose own text says "Please retry." is not echoed: the answer is the do-not-generate-again text, without the
    ///     conflict marker. Red: quote the refusal after the tasks' count, as before; the answer says "Please retry.".
    /// </summary>
    [Fact]
    public async Task Generated_tasks_whose_session_save_fails_with_a_retry_hint_do_not_echo_it()
    {
        var session = TaskCreationSessionOnEveryRead();
        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("The database was busy. Please retry."));

        var result = await _service.GenerateTasksAsync(session.Id, CancellationToken.None);

        result.Error.Should().Be(CompletionNotSaved);
        result.Error.Should().NotContainEquivalentOf("retry").And.NotContainEquivalentOf(SaveConflict.Marker);
    }

    private const string CompletionNotSaved =
        "Created 2 tasks, but the session could not be marked complete. The tasks exist; do not generate them again.";

    /// <summary>
    ///     A session in TaskCreation with a plan, read afresh on every <c>GetByIdAsync</c>, as the repository reads it;
    ///     the model and the conversion answer two tasks.
    /// </summary>
    private BrainstormSession TaskCreationSessionOnEveryRead()
    {
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        BrainstormSession Read() => BrainstormSession.Rehydrate(
            id, projectId, BrainstormPhase.TaskCreation, "design", "### Task 1: Create entity", false, DateTime.UtcNow,
            null, 1, []);
        _repository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(_ => Result<BrainstormSession>.Success(Read()));
        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmInvocationResult>.Success(new LlmInvocationResult
            {
                Response = """[{"title":"Create entity","description":"d","acceptanceCriteria":"a","priority":"High","complexity":"Low","dependencies":[],"phase":"P1","parallelGroup":1}]""",
            }));
        _prdService.ConvertToTasksAsync(projectId, Arg.Any<IReadOnlyList<PrdItemForConversionDto>>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<TaskDto>>.Success([null!, null!]));
        return Read();
    }
}
