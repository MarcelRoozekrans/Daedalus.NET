using Daedalus.Api.Endpoints;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services.Brainstorm;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Repositories;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Persistence;

/// <summary>
///     Phase 2.14, task 1: a message added to a stored brainstorm session is kept. The browser scenario saw a sent
///     message missing from the next read of the session. Phase 3.5 adds the service's create, whose save follows an add,
///     and an abandon that loses a race, through the real service and repository on ZeroAlloc.ORM.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BrainstormMessagePersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly IAgentFactory _agent = Substitute.For<IAgentFactory>();
    private Guid _projectId;
    private Guid _sessionId;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        var project = IntegrationTestFactory.CreateProject();
        (await new ProjectRepository(fixture.Db, NullLogger<ProjectRepository>.Instance)
            .AddAsync(project, CancellationToken.None)).IsSuccess.Should().BeTrue();
        _projectId = project.Id;

        var session = BrainstormSession.Create(project.Id).Value;
        session.AddMessage(MessageRole.Assistant, "Who is it for?").IsSuccess.Should().BeTrue();
        (await Repository().AddAsync(session, CancellationToken.None)).IsSuccess.Should().BeTrue();
        _sessionId = session.Id;
        _agent.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmInvocationResult>.Success(new LlmInvocationResult { Response = "What do they count?" }));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private BrainstormRepository Repository() => new(fixture.Db, Substitute.For<ILogger<BrainstormRepository>>());

    private BrainstormService Service(IBrainstormRepository repository) =>
        new(repository, _agent, new ProjectRepository(fixture.Db, NullLogger<ProjectRepository>.Instance),
            Substitute.For<IPrdService>(), NullLogger<BrainstormService>.Instance);

    /// <summary>
    ///     As <c>BrainstormService.SendMessageAsync</c> does: read the session, add two messages, update.
    ///     Red: skip the message insert in <c>BrainstormSql.SaveAsync</c>; the two messages are not stored.
    /// </summary>
    [Fact]
    public async Task A_message_added_to_a_stored_session_is_kept()
    {
        var repository = Repository();
        var session = (await repository.GetByIdAsync(_sessionId, CancellationToken.None)).Value;
        session.AddMessage(MessageRole.User, "Shop owners.").IsSuccess.Should().BeTrue();
        session.AddMessage(MessageRole.Assistant, "What do they count?").IsSuccess.Should().BeTrue();

        var updated = await repository.UpdateAsync(session, CancellationToken.None);

        updated.IsSuccess.Should().BeTrue(updated.IsFailure ? updated.Error : string.Empty);
        var contents = (await Repository().GetByIdAsync(_sessionId, CancellationToken.None)).Value.Messages
            .Select(m => m.Content).ToList();
        contents.Should().Equal("Who is it for?", "Shop owners.", "What do they count?");
    }

    /// <summary>
    ///     Ruling R77: <c>CreateSessionAsync</c> adds the session, then saves it with the model's opening message. The
    ///     save is guarded by the row version the session was read with, and an add reads none back, so the service
    ///     reads the stored session before it changes it. Red: save the added instance, as before the port; the save is
    ///     refused as modified by another operation and the opening message is lost.
    /// </summary>
    [Fact]
    public async Task A_created_session_keeps_its_opening_message()
    {
        var created = await Service(Repository()).CreateSessionAsync(_projectId, CancellationToken.None);

        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error : string.Empty);
        var stored = (await Repository().GetByIdAsync(created.Value.Id, CancellationToken.None)).Value;
        stored.Messages.Select(m => m.Content).Should().Equal("What do they count?");
    }

    /// <summary>
    ///     Owner decision of 2026-11-05, ruling R83: an abandon whose save lost a race answers 409 with the retry
    ///     message, through the real service and repository. Another connection moves the session to Clarification
    ///     between the service's load and its save; that write stands. Red: answer every failure of AbandonSession with
    ///     400, as before; the lost race answers 400.
    /// </summary>
    [Fact]
    public async Task An_abandon_that_loses_a_race_answers_409_with_the_retry_message()
    {
        var racing = new RacingBrainstormRepository(Repository(), fixture.ConnectionString);

        var result = (await BrainstormEndpoints.AbandonSession(
            _sessionId, Service(racing), NullLoggerFactory.Instance, CancellationToken.None)).Result;

        var json = result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        json.Value.Should().Be(new ErrorBody("The brainstorm session was modified by another operation. Please retry."));
        var stored = (await Repository().GetByIdAsync(_sessionId, CancellationToken.None)).Value;
        stored.Phase.Should().Be(BrainstormPhase.Clarification);
        stored.CompletedAt.Should().BeNull();
    }

    /// <summary>Moves the session to Clarification over a second connection just before each save, as a concurrent request would.</summary>
    private sealed class RacingBrainstormRepository(IBrainstormRepository inner, string connectionString)
        : IBrainstormRepository
    {
        public Task<Result<BrainstormSession>> AddAsync(BrainstormSession session, CancellationToken ct) =>
            inner.AddAsync(session, ct);

        public Task<Result<BrainstormSession>> GetByIdAsync(Guid id, CancellationToken ct) => inner.GetByIdAsync(id, ct);

        public Task<Result<IReadOnlyList<BrainstormSession>>> GetByProjectIdAsync(Guid projectId, CancellationToken ct) =>
            inner.GetByProjectIdAsync(projectId, ct);

        public async Task<Result> UpdateAsync(BrainstormSession session, CancellationToken ct)
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(ct);
                await using var command = new NpgsqlCommand(
                    """UPDATE "BrainstormSessions" SET "Phase" = 1 WHERE "Id" = @id""", connection);
                command.Parameters.AddWithValue("id", session.Id);
                await command.ExecuteNonQueryAsync(ct);
            }

            return await inner.UpdateAsync(session, ct);
        }
    }
}
