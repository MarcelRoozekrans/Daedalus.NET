using AwesomeAssertions.Equivalency;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Repositories;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     Phase 3.5: the brainstorm repository on ZeroAlloc.ORM stores and loads every column of a session and its messages,
///     loads a row whatever <c>Create</c> would say of it today, refuses a guarded save that lost a race with "modified
///     by another operation" (#290), writes the session and its new messages in one transaction, and keeps today's
///     family of answers for a session deleted under a save.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BrainstormRepositoryRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    public async SystemTask InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public SystemTask DisposeAsync() => SystemTask.CompletedTask;

    private BrainstormRepository Repository() => new(fixture.Db, NullLogger<BrainstormRepository>.Instance);

    /// <summary>
    ///     A session stored through the repository with every column set and two messages comes back equal in every
    ///     property, the messages oldest first, with a row version and UTC times; the project's list carries it without
    ///     messages. Red, shown once: read <c>"ImplementationPlan"</c> into <c>DesignDocument</c> in
    ///     <c>FindWithMessagesRowsQuery</c>, and the comparison names <c>DesignDocument</c>.
    /// </summary>
    [Fact]
    public async SystemTask Stored_session_loads_back_whole()
    {
        var projectId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var newer = BrainstormMessage.Rehydrate(
            Guid.NewGuid(), id, MessageRole.Assistant, "Second", BrainstormPhase.Proposals, _at.AddMinutes(2));
        var older = BrainstormMessage.Rehydrate(
            Guid.NewGuid(), id, MessageRole.User, "First", BrainstormPhase.Clarification, _at.AddMinutes(1));
        var session = BrainstormSession.Rehydrate(
            id, projectId, BrainstormPhase.Completed, "design", "plan", true, _at, _at.AddHours(1), 0, [newer, older]);

        (await Repository().AddAsync(session, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var loaded = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        loaded.Should().BeEquivalentTo(Expected(session, [older, newer]), Members);
        loaded.Messages.Select(m => m.Content).Should().Equal("First", "Second");
        loaded.RowVersion.Should().NotBe(0u);
        loaded.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        loaded.Messages[0].CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        var listed = (await Repository().GetByProjectIdAsync(projectId, CancellationToken.None)).Value;
        listed.Should().ContainSingle().Which.Should().BeEquivalentTo(Expected(session, []), Members);
    }

    /// <summary>
    ///     Review focus 1: a session row with an empty project id and a message with blank content, both refused by
    ///     <c>Create</c>, and bytes in the dead <c>bytea</c> column, still load as written. Red: build the loaded message
    ///     through <c>BrainstormMessage.Create</c>, and the load fails.
    /// </summary>
    [Fact]
    public async SystemTask A_row_Create_rejects_still_loads()
    {
        var id = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        BrainstormSession.Create(Guid.Empty).IsFailure.Should().BeTrue("the row below is one Create refuses");
        BrainstormMessage.Create(id, MessageRole.User, " ", BrainstormPhase.ContextGathering).IsFailure.Should()
            .BeTrue("the message below is one Create refuses");
        await ExecuteAsync(
            """
            INSERT INTO "BrainstormSessions"
                ("Id", "ProjectId", "Phase", "DesignDocument", "ImplementationPlan", "PhaseCompleteSignaled", "CreatedAt",
                 "CompletedAt")
            VALUES (@id, @empty, 7, NULL, NULL, FALSE, @at, NULL);
            INSERT INTO "BrainstormMessages" ("Id", "BrainstormSessionId", "Role", "Content", "Phase", "CreatedAt")
            VALUES (@messageId, @id, 2, ' ', 0, @at);
            """,
            ("id", id), ("empty", Guid.Empty), ("at", _at), ("messageId", messageId));
        var expected = BrainstormSession.Rehydrate(
            id, Guid.Empty, BrainstormPhase.Abandoned, null, null, false, _at, null, 0,
            [BrainstormMessage.Rehydrate(messageId, id, MessageRole.User, " ", BrainstormPhase.ContextGathering, _at)]);

        var loaded = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;

        loaded.Should().BeEquivalentTo(expected, Members);
    }

    /// <summary>
    ///     Review focus 2, #290: two reads of one session; the first adds a message and is saved, the second abandons the
    ///     session and adds a message of its own, and is refused with "modified by another operation". The first save's
    ///     values and message stand, and none of the second's is stored. Red, shown against EF Core before the port,
    ///     whose <c>bytea</c> token was never set: the second save succeeded. Red on the port: in
    ///     <c>BrainstormSql.SaveAsync</c>, pass the row version re-read just before the write instead of the one the
    ///     session carries; the second save succeeds.
    /// </summary>
    [Fact]
    public async SystemTask UpdateAsync_loses_the_race_with_ModifiedByAnotherOperation()
    {
        var id = await AddSessionAsync();
        var first = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        var second = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        first.AddMessage(MessageRole.User, "First [PHASE_COMPLETE]").IsSuccess.Should().BeTrue();
        second.AddMessage(MessageRole.User, "Second").IsSuccess.Should().BeTrue();
        second.Abandon().IsSuccess.Should().BeTrue();

        (await Repository().UpdateAsync(first, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var result = await Repository().UpdateAsync(second, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("The brainstorm session was modified by another operation. Please retry.");
        var stored = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        stored.Phase.Should().Be(BrainstormPhase.ContextGathering);
        stored.PhaseCompleteSignaled.Should().BeTrue();
        stored.CompletedAt.Should().BeNull();
        stored.Messages.Select(m => m.Content).Should().Equal("Opening", "First");
    }

    /// <summary>
    ///     The session's columns and its new messages are written in one transaction: a message insert that fails, the
    ///     later of the two writes, rolls back the session update too. A trigger refuses the message with P0001, not a
    ///     unique violation, so <c>ON CONFLICT</c> does not absorb it. Red: commit the update in a transaction of its
    ///     own before the insert in <c>BrainstormSql.SaveAsync</c>; the phase is stored without the message.
    /// </summary>
    [Fact]
    public async SystemTask UpdateAsync_writes_the_session_and_its_messages_in_one_transaction()
    {
        var id = await AddSessionAsync();
        var session = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        session.AddMessage(MessageRole.User, "refused").IsSuccess.Should().BeTrue();
        session.Abandon().IsSuccess.Should().BeTrue();
        await ExecuteAsync(
            """
            CREATE FUNCTION refuse_brainstorm_message() RETURNS trigger AS $$
            BEGIN
                IF NEW."Content" = 'refused' THEN
                    RAISE EXCEPTION 'message refused' USING ERRCODE = 'P0001';
                END IF;
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER refuse_brainstorm_message BEFORE INSERT ON "BrainstormMessages"
                FOR EACH ROW EXECUTE FUNCTION refuse_brainstorm_message();
            """);
        Result result;
        try
        {
            result = await Repository().UpdateAsync(session, CancellationToken.None);
        }
        finally
        {
            await ExecuteAsync(
                """DROP TRIGGER refuse_brainstorm_message ON "BrainstormMessages"; DROP FUNCTION refuse_brainstorm_message();""");
        }

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Failed to update brainstorm session {id}. The cause is logged.");
        var stored = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        stored.Phase.Should().Be(BrainstormPhase.ContextGathering);
        stored.CompletedAt.Should().BeNull();
        stored.Messages.Select(m => m.Content).Should().Equal("Opening");
    }

    /// <summary>
    ///     A save inserts the messages added since the read and skips the stored ones, so a second save of a re-read
    ///     session with one more message stores that one and duplicates none. Red: drop <c>ON CONFLICT ("Id") DO
    ///     NOTHING</c> from the message insert; the save fails on the stored message's key.
    /// </summary>
    [Fact]
    public async SystemTask UpdateAsync_stores_the_added_messages_and_skips_the_stored_ones()
    {
        var id = await AddSessionAsync();
        var session = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        session.AddMessage(MessageRole.User, "One").IsSuccess.Should().BeTrue();
        (await Repository().UpdateAsync(session, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var reread = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        reread.AddMessage(MessageRole.Assistant, "Two").IsSuccess.Should().BeTrue();

        var result = await Repository().UpdateAsync(reread, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : string.Empty);
        (await Repository().GetByIdAsync(id, CancellationToken.None)).Value.Messages.Select(m => m.Content).Should()
            .Equal("Opening", "One", "Two");
    }

    /// <summary>
    ///     Review focus 3, ruling R78: a session deleted after its load, saved with a new message, keeps EF Core's family
    ///     of answer, the logged "Failed to update brainstorm session ..." (EF's insert of the message broke the foreign
    ///     key; shown against EF Core before the port), now with the generic text of R79. Red: answer every
    ///     <c>ZeroAllocOrmConcurrencyException</c> as modified by another operation; the deleted session answers that
    ///     instead.
    /// </summary>
    [Fact]
    public async SystemTask UpdateAsync_of_a_session_deleted_since_its_load_keeps_todays_error()
    {
        var id = await AddSessionAsync();
        var read = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        read.AddMessage(MessageRole.User, "Too late").IsSuccess.Should().BeTrue();
        await ExecuteAsync("""DELETE FROM "BrainstormSessions" WHERE "Id" = @id""", ("id", id));

        var result = await Repository().UpdateAsync(read, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Failed to update brainstorm session {id}. The cause is logged.");
        (await Repository().GetByIdAsync(id, CancellationToken.None)).Error.Should()
            .Be($"Brainstorm session {id} not found.");
    }

    /// <summary>
    ///     Review focus 3, ruling R78: a session deleted after its load, saved with only its columns changed, an abandon,
    ///     answers the same logged failure. EF Core answered "modified by another operation" here, since its update
    ///     matched no row and no message was inserted; the port cannot tell this save from one with a new message
    ///     without tracking, and a deleted session is not a race to retry. Red: answer every
    ///     <c>ZeroAllocOrmConcurrencyException</c> as modified by another operation; the abandon answers that instead.
    /// </summary>
    [Fact]
    public async SystemTask UpdateAsync_of_an_abandon_of_a_session_deleted_since_its_load_answers_the_failure()
    {
        var id = await AddSessionAsync();
        var read = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        read.Abandon().IsSuccess.Should().BeTrue();
        await ExecuteAsync("""DELETE FROM "BrainstormSessions" WHERE "Id" = @id""", ("id", id));

        var result = await Repository().UpdateAsync(read, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Failed to update brainstorm session {id}. The cause is logged.");
    }

    /// <summary>
    ///     The save writes the five columns the domain methods change and nothing else: a project id and creation time
    ///     that differ in memory are not stored. Red: also set <c>"ProjectId"</c> in <c>UpdateQuery</c>; the stored
    ///     project id changes.
    /// </summary>
    [Fact]
    public async SystemTask Update_writes_only_the_columns_its_domain_methods_change()
    {
        var id = await AddSessionAsync();
        var read = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        var stored = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        var changed = BrainstormSession.Rehydrate(
            id, Guid.NewGuid(), BrainstormPhase.PlanGeneration, "design", "plan", true, _at.AddDays(1), _at.AddHours(2),
            read.RowVersion, read.Messages);

        (await Repository().UpdateAsync(changed, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var loaded = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        loaded.ProjectId.Should().Be(stored.ProjectId);
        loaded.CreatedAt.Should().Be(stored.CreatedAt);
        loaded.Phase.Should().Be(BrainstormPhase.PlanGeneration);
        loaded.DesignDocument.Should().Be("design");
        loaded.ImplementationPlan.Should().Be("plan");
        loaded.PhaseCompleteSignaled.Should().BeTrue();
        loaded.CompletedAt.Should().Be(_at.AddHours(2));
    }

    /// <summary>
    ///     Sessions with one creation time list by id after it, both descending (ruling R80). The tie order has no
    ///     reliable red: without <c>ThenByDescending(s => s.Id)</c> the order of tied rows is undefined, so the test pins
    ///     the order the tie-breaker defines.
    /// </summary>
    [Fact]
    public async SystemTask Sessions_of_a_project_list_newest_first_then_by_id()
    {
        var projectId = Guid.NewGuid();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var session = BrainstormSession.Rehydrate(
                Guid.NewGuid(), projectId, BrainstormPhase.ContextGathering, null, null, false, _at, null, 0, []);
            (await Repository().AddAsync(session, CancellationToken.None)).IsSuccess.Should().BeTrue();
            ids.Add(session.Id);
        }

        var newest = BrainstormSession.Rehydrate(
            Guid.NewGuid(), projectId, BrainstormPhase.ContextGathering, null, null, false, _at.AddMinutes(1), null, 0, []);
        (await Repository().AddAsync(newest, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var listed = (await Repository().GetByProjectIdAsync(projectId, CancellationToken.None)).Value;

        listed.Select(s => s.Id).Should().Equal([newest.Id, .. ids.OrderDescending()]);
    }

    private static Func<EquivalencyOptions<BrainstormSession>, EquivalencyOptions<BrainstormSession>> Members =>
        o => o.ComparingByMembers<BrainstormSession>().ComparingByMembers<BrainstormMessage>()
            .WithStrictOrderingFor(s => s.Messages)
            .Excluding((IMemberInfo m) => m.Name == nameof(BrainstormSession.RowVersion));

    private static BrainstormSession Expected(BrainstormSession session, IEnumerable<BrainstormMessage> messages) =>
        BrainstormSession.Rehydrate(
            session.Id, session.ProjectId, session.Phase, session.DesignDocument, session.ImplementationPlan,
            session.PhaseCompleteSignaled, session.CreatedAt, session.CompletedAt, 0, messages);

    private async System.Threading.Tasks.Task<Guid> AddSessionAsync()
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        session.AddMessage(MessageRole.Assistant, "Opening").IsSuccess.Should().BeTrue();
        (await Repository().AddAsync(session, CancellationToken.None)).IsSuccess.Should().BeTrue();
        return session.Id;
    }

    private async SystemTask ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
