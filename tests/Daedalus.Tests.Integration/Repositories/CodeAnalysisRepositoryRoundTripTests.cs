using System.Diagnostics;
using AwesomeAssertions.Equivalency;
using Daedalus.Api.Endpoints;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Orm.CodeAnalysisRequests;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ZeroAlloc.ORM;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     Phase 3.5: the code analysis repository on ZeroAlloc.ORM stores and loads every column of a request, its owned
///     values and its iterations, loads a row whatever <c>Create</c> would say of it today, refuses each of the six
///     guarded saves that lost a race with "modified by another operation" (#290), records an iteration in one
///     transaction, keeps today's family of answers for a request deleted under a save, and lets the two independent
///     saves through a concurrent write.
/// </summary>
/// <remarks>
///     A race here is a real second writer: another connection changes the row in an open transaction, the repository
///     method loads the committed row and blocks on its own write, and the other transaction then commits. PostgreSQL
///     checks the waiting <c>UPDATE</c>'s <c>WHERE</c> again against the committed row, so the order is fixed and the
///     repository's write is always the later one.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class CodeAnalysisRepositoryRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Refusal = "The analysis request was modified by another operation. Please retry.";

    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    public static TheoryData<string> GuardedMethods =>
        ["UpdateStatus", "UpdateIteration", "RecordValidation", "Complete", "Fail", "Cancel"];

    public static TheoryData<string> IndependentMethods => ["UpdateWorkTreePath", "UpdateLastPrompt"];

    public async SystemTask InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public SystemTask DisposeAsync() => SystemTask.CompletedTask;

    private readonly CapturingLogger _log = new();

    private CodeAnalysisRepository Repository() => new(fixture.Db, _log);

    /// <summary>
    ///     A request stored through the repository with every column set, its owned values and two iterations comes back
    ///     equal in every property, the iterations by number, with a row version and UTC times; the three lists carry it
    ///     without iterations. Red, shown once: read <c>r.Outcome.CommitShaFinal</c> into <c>PullRequestUrl</c> in
    ///     <c>FindWithIterationsRowsQuery</c>, and the comparison names <c>Outcome.PullRequestUrl</c>; and once, with the
    ///     same run: read <c>i.AiResponse</c> into <c>PromptSent</c> of the iteration row, and
    ///     <c>A_row_Create_rejects_still_loads</c> names <c>Iterations[0].PromptSent</c>.
    /// </summary>
    [Fact]
    public async SystemTask Stored_request_loads_back_whole()
    {
        var id = Guid.NewGuid();
        var second = Iteration(id, 2, "second prompt");
        var first = Iteration(id, 1, "first prompt");
        var request = CodeAnalysisRequest.Rehydrate(
            id, "Title", "Description",
            RepositoryReference.Rehydrate("https://github.com/org/repo", "main", "abc123", RepositoryPlatform.GitHub),
            CodeLocation.Rehydrate("src/a.cs", 3, 9),
            AnalysisType.SecurityAudit, 4, """["r1", "r2"]""", """["doc"]""", """{"a": 1}""", "/tmp/tree",
            "feature/x", AnalysisStatus.Pending, 2, 10, "goals", "last prompt", "last response",
            AnalysisOutcome.Rehydrate("https://github.com/org/repo/pull/1", "def456", """{"ok": true}""", true),
            _at, _at.AddMinutes(1), _at.AddMinutes(2), _at.AddMinutes(3), "reason", Guid.NewGuid(), Guid.NewGuid(), 0,
            [second, first]);

        (await Repository().CreateAsync(request)).IsSuccess.Should().BeTrue();

        var loaded = (await Repository().GetByIdAsync(id)).Value;
        loaded.Should().BeEquivalentTo(Expected(request, [first, second]), Members);
        loaded.Iterations.Select(i => i.IterationNumber).Should().Equal(1, 2);
        loaded.RowVersion.Should().NotBe(0u);
        loaded.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        loaded.Iterations[0].CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        var withoutIterations = Expected(request, []);
        (await Repository().GetPendingAsync(10)).Value.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(withoutIterations, Members);
        (await Repository().GetByStatusAsync(AnalysisStatus.Pending)).Value.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(withoutIterations, Members);
        (await Repository().GetByRepositoryAsync("https://github.com/org/repo")).Value.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(withoutIterations, Members);
    }

    /// <summary>
    ///     Review focus 1: a row with an empty title, a start line after its end line, a platform the URL would not
    ///     detect today, bytes in the dead <c>bytea</c> column, and an iteration numbered 0 with a blank prompt, all of
    ///     which <c>Create</c> refuses or would change, still loads as written. Red: build the location through
    ///     <c>CodeLocation.Create</c> in <c>CodeAnalysisRequestRow.ToDomain</c>, and the load fails; or the reference
    ///     through <c>RepositoryReference.Create</c>, and the platform reads GitHub.
    /// </summary>
    [Fact]
    public async SystemTask A_row_Create_rejects_still_loads()
    {
        var id = Guid.NewGuid();
        var iterationId = Guid.NewGuid();
        CodeLocation.Create("f", 9, 3).IsFailure.Should().BeTrue("the location below is one Create refuses");
        RepositoryReference.Create("https://github.com/org/repo").Value.Platform.Should()
            .Be(RepositoryPlatform.GitHub, "the row below stores another platform for this URL");
        AnalysisIteration.Create(Guid.NewGuid(), id, 0, " ", "r").IsFailure.Should()
            .BeTrue("the iteration below is one Create refuses");
        await ExecuteAsync(
            """
            INSERT INTO "CodeAnalysisRequests"
                ("Id", "Title", "Description", "RepositoryUrl", "RepositoryBranch", "CommitSha", "Platform", "FilePath",
                 "StartLine", "EndLine", "Type", "Priority", "Requirements", "Status", "CurrentIteration",
                 "MaxIterations", "HasFailedValidation", "CreatedAt")
            VALUES (@id, '', ' ', 'https://github.com/org/repo', NULL, NULL, 0, 'f', 9, 3, 0, -1, '[]', 0, 0, 0,
                    FALSE, @at);
            INSERT INTO "AnalysisIterations"
                ("Id", "CodeAnalysisRequestId", "IterationNumber", "PromptSent", "AiResponse", "CreatedAt")
            VALUES (@iterationId, @id, 0, ' ', 'r', @at);
            """,
            ("id", id), ("at", _at), ("iterationId", iterationId));
        var expected = CodeAnalysisRequest.Rehydrate(
            id, "", " ", RepositoryReference.Rehydrate("https://github.com/org/repo", null, null, RepositoryPlatform.None),
            CodeLocation.Rehydrate("f", 9, 3), AnalysisType.None, -1, "[]", null, null, null, null, AnalysisStatus.None,
            0, 0, null, null, null, AnalysisOutcome.Rehydrate(null, null, null, false), _at, null, null, null, null,
            null, null, 0,
            [AnalysisIteration.Rehydrate(iterationId, id, 0, null, null, " ", "r", null, null, null, 0, 0, null, _at)]);

        var loaded = (await Repository().GetByIdAsync(id)).Value;

        loaded.Should().BeEquivalentTo(expected, Members);
    }

    /// <summary>
    ///     Review focus 2, #290: each guarded write loads the request, and before its save lands another write fails the
    ///     request and commits. The guarded write is refused with "modified by another operation", the other write's
    ///     values stand, and nothing of the refused write is stored, no iteration either. Red, shown for every method:
    ///     save through a copy of <c>SaveLifecycleQuery</c> that filters on the id alone; each save succeeds and
    ///     overwrites the status.
    /// </summary>
    [Theory]
    [MemberData(nameof(GuardedMethods))]
    public async SystemTask A_guarded_save_loses_the_race_with_ModifiedByAnotherOperation(string method)
    {
        var id = await AddRequestAsync(StatusFor(method));

        var result = await WhileAnotherWriteHoldsTheRowAsync(
            """
            UPDATE "CodeAnalysisRequests"
            SET "Status" = 9, "FailureReason" = 'first writer', "CompletedAt" = @at WHERE "Id" = @id
            """,
            id,
            () => Guarded(method, id));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(Refusal);
        var stored = (await Repository().GetByIdAsync(id)).Value;
        stored.Status.Should().Be(AnalysisStatus.Failed);
        stored.FailureReason.Should().Be("first writer");
        stored.CompletedAt.Should().Be(_at);
        stored.StartedAt.Should().BeNull();
        stored.CancelledAt.Should().BeNull();
        stored.CurrentIteration.Should().Be(0);
        stored.Iterations.Should().BeEmpty();
        stored.Outcome.Should().BeEquivalentTo(AnalysisOutcome.Empty(), o => o.ComparingByMembers<AnalysisOutcome>());
    }

    /// <summary>
    ///     Review focus 3: a request deleted after a guarded write loaded it keeps EF Core's family of answer, the
    ///     method's logged failure (EF's update matched no row and its <c>DbUpdateConcurrencyException</c> fell into the
    ///     method's <c>catch</c>), now with the generic text, not the retry text (ruling R90). Red: answer every
    ///     <c>ZeroAllocOrmConcurrencyException</c> as modified by another operation; the deleted request answers that
    ///     instead. Red, shown: drop the <c>logError</c> call on the deleted path of <c>SaveGuardedAsync</c>; no Error
    ///     entry carries the conflict.
    /// </summary>
    [Theory]
    [MemberData(nameof(GuardedMethods))]
    public async SystemTask A_guarded_save_of_a_request_deleted_since_its_load_keeps_todays_error(string method)
    {
        var id = await AddRequestAsync(StatusFor(method));

        var result = await WhileAnotherWriteHoldsTheRowAsync(
            """DELETE FROM "CodeAnalysisRequests" WHERE "Id" = @id""",
            id,
            () => Guarded(method, id));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().StartWith("Error ").And.EndWith($"analysis request {id}. The cause is logged.");
        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Exception.Should().BeOfType<ZeroAllocOrmConcurrencyException>();
        (await Repository().GetByIdAsync(id)).Error.Should().Be($"Analysis request {id} not found");
    }

    /// <summary>
    ///     Review focus 3 for the independent writes: a request deleted after the work tree path or the last prompt
    ///     write loaded it answers the method's logged failure, as EF's update of a deleted row did, with the generic
    ///     text (R89, R94), and the refused write is logged. Red, shown for both: drop <c>ThrowOnNoRows = true</c> from
    ///     <c>SetWorkTreePath</c> and <c>SetLastPrompt</c>; the write of no row answers success.
    /// </summary>
    [Theory]
    [MemberData(nameof(IndependentMethods))]
    public async SystemTask An_independent_save_of_a_request_deleted_since_its_load_answers_its_failure(string method)
    {
        var id = await AddRequestAsync(AnalysisStatus.Ready);

        var result = await WhileAnotherWriteHoldsTheRowAsync(
            """DELETE FROM "CodeAnalysisRequests" WHERE "Id" = @id""",
            id,
            () => Independent(method, id));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(string.Equals(method, "UpdateWorkTreePath", StringComparison.Ordinal)
            ? $"Error updating work tree path of analysis request {id}. The cause is logged."
            : $"Error updating last prompt of analysis request {id}. The cause is logged.");
        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Exception.Should().BeOfType<ZeroAllocOrmConcurrencyException>();
    }

    /// <summary>
    ///     An iteration is recorded in one transaction: an iteration insert that fails, the later of the two writes,
    ///     rolls back the request's new iteration count too. A trigger refuses the insert with P0001. Red: commit the
    ///     lifecycle update in a transaction of its own before the insert in <c>CodeAnalysisSql.SaveIterationAsync</c>;
    ///     the count is stored as 1 without the iteration.
    /// </summary>
    [Fact]
    public async SystemTask UpdateIterationAsync_writes_the_count_and_the_iteration_in_one_transaction()
    {
        var id = await AddRequestAsync(AnalysisStatus.AnalysisInProgress);
        await ExecuteAsync(
            """
            CREATE FUNCTION refuse_analysis_iteration() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'iteration refused' USING ERRCODE = 'P0001';
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER refuse_analysis_iteration BEFORE INSERT ON "AnalysisIterations"
                FOR EACH ROW EXECUTE FUNCTION refuse_analysis_iteration();
            """);
        Result result;
        try
        {
            result = await Repository().UpdateIterationAsync(id, 1, "prompt", "response");
        }
        finally
        {
            await ExecuteAsync(
                """DROP TRIGGER refuse_analysis_iteration ON "AnalysisIterations"; DROP FUNCTION refuse_analysis_iteration();""");
        }

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Error updating iteration of analysis request {id}. The cause is logged.");
        var stored = (await Repository().GetByIdAsync(id)).Value;
        stored.CurrentIteration.Should().Be(0);
        stored.Iterations.Should().BeEmpty();
    }

    /// <summary>
    ///     Owner decision 5, the benign case: an independent write, the work tree path or the last prompt, loads the
    ///     request while another write fails it, and still succeeds once that write commits; both writes' values stand.
    ///     Red, shown for both: run the guarded lifecycle save of the loaded request before the column write; the write
    ///     is refused. Red, shown for both: also set the status to Ready, the one read before the other write
    ///     committed, in the column write; the status reads Ready again.
    /// </summary>
    [Theory]
    [MemberData(nameof(IndependentMethods))]
    public async SystemTask An_independent_save_does_not_fail_on_a_concurrent_write(string method)
    {
        var id = await AddRequestAsync(AnalysisStatus.Ready);

        var result = await WhileAnotherWriteHoldsTheRowAsync(
            """
            UPDATE "CodeAnalysisRequests"
            SET "Status" = 9, "FailureReason" = 'first writer', "CompletedAt" = @at WHERE "Id" = @id
            """,
            id,
            () => Independent(method, id));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : string.Empty);
        var stored = (await Repository().GetByIdAsync(id)).Value;
        stored.Status.Should().Be(AnalysisStatus.Failed);
        stored.FailureReason.Should().Be("first writer");
        if (string.Equals(method, "UpdateWorkTreePath", StringComparison.Ordinal))
        {
            stored.LocalWorkTreePath.Should().Be("/tmp/new-tree");
            stored.LastPromptSent.Should().Be("old prompt");
        }
        else
        {
            stored.LocalWorkTreePath.Should().Be("/tmp/old-tree");
            stored.LastPromptSent.Should().Be("new prompt");
            stored.LastAiResponse.Should().Be("new response");
        }
    }

    /// <summary>
    ///     Ruling R97: the reverse of the benign case does not hold under an <c>xmin</c> guard. A guarded write that
    ///     loaded the request before an independent write committed is refused with the retry text, though the two
    ///     write different columns; nothing is lost, and a retry succeeds. Pinned so a change to that trade-off is a
    ///     decision, not an accident.
    /// </summary>
    [Fact]
    public async SystemTask A_guarded_save_after_a_concurrent_independent_write_answers_the_retry_text()
    {
        var id = await AddRequestAsync(AnalysisStatus.AnalysisInProgress);

        var result = await WhileAnotherWriteHoldsTheRowAsync(
            """UPDATE "CodeAnalysisRequests" SET "LocalWorkTreePath" = 'other' WHERE "Id" = @id""",
            id,
            () => Repository().CancelAsync(id));

        result.Error.Should().Be(Refusal);
        (await Repository().CancelAsync(id)).IsSuccess.Should().BeTrue("a retry reads the new row version");
        var stored = (await Repository().GetByIdAsync(id)).Value;
        stored.Status.Should().Be(AnalysisStatus.Cancelled);
        stored.LocalWorkTreePath.Should().Be("other");
    }

    /// <summary>
    ///     Owner decision of 2026-11-05 (ruling R95), end to end: the cancel route, with the real repository, whose save
    ///     loses a race to another connection's write between its load and its save, answers 409 with the retry
    ///     message. Red, shown: drop the conflict branch of <c>CancelAnalysis</c>; the route answers 400.
    /// </summary>
    [Fact]
    public async SystemTask Cancel_that_loses_a_race_answers_409_with_the_retry_message()
    {
        var id = await AddRequestAsync(AnalysisStatus.AnalysisInProgress);
        await using var services = new ServiceCollection().AddLogging().AddDaedalusEndpoints().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.SetEndpoint(new Endpoint(
            _ => SystemTask.CompletedTask, new EndpointMetadataCollection(ProblemContentType.Json), "test"));
        IResult? answer = null;

        await WhileAnotherWriteHoldsTheRowAsync(
            """UPDATE "CodeAnalysisRequests" SET "FailureReason" = 'first writer' WHERE "Id" = @id""",
            id,
            async () =>
            {
                answer = (await CodeAnalysisEndpoints.CancelAnalysis(
                    id, context, Repository(), NullLoggerFactory.Instance, CancellationToken.None)).Result;
                return Result.Success();
            });

        var problem = answer.Should().BeOfType<ApiProblemResult>().Subject;
        problem.StatusCode.Should().Be(409);
        problem.ProblemDetails.Detail.Should().Be(Refusal);
        (await Repository().GetByIdAsync(id)).Value.Status.Should().Be(AnalysisStatus.AnalysisInProgress);
    }

    /// <summary>
    ///     A lifecycle save writes the ten lifecycle columns and nothing else: a title, a work tree path, a last prompt
    ///     and a requirements document that differ in memory are not stored. Red: also set <c>"LastPromptSent"</c> in
    ///     <c>SaveLifecycleQuery</c>; the stored prompt changes.
    /// </summary>
    [Fact]
    public async SystemTask A_lifecycle_save_writes_only_the_lifecycle_columns()
    {
        var id = await AddRequestAsync(AnalysisStatus.AnalysisInProgress);
        var read = (await Repository().GetByIdAsync(id)).Value;
        var changed = CodeAnalysisRequest.Rehydrate(
            id, "changed title", "changed description",
            RepositoryReference.Rehydrate("https://example.com/other", "dev", "fff", RepositoryPlatform.Gitea),
            CodeLocation.Rehydrate("other.cs", 1, 2), AnalysisType.TestGeneration, 9, """["changed"]""", "[1]", "[2]",
            "/tmp/changed", "changed-branch", AnalysisStatus.Completed, 3, 50, "changed goals", "changed prompt",
            "changed response",
            AnalysisOutcome.Rehydrate("https://pr", "sha", """{"v": 1}""", true),
            _at.AddDays(1), _at.AddMinutes(1), _at.AddMinutes(2), _at.AddMinutes(3), "why", Guid.NewGuid(),
            Guid.NewGuid(), read.RowVersion, []);

        await using (var connection = await fixture.Db.OpenAsync(CancellationToken.None))
        {
            await new CodeAnalysisSql(connection).SaveLifecycleAsync(changed, CancellationToken.None);
        }

        var loaded = (await Repository().GetByIdAsync(id)).Value;
        var expected = CodeAnalysisRequest.Rehydrate(
            id, read.Title, read.Description, read.Repository, read.Location, read.Type, read.Priority,
            read.Requirements, read.ReferencedDocuments, read.ExternalReferences, read.LocalWorkTreePath,
            read.FeatureBranchName, AnalysisStatus.Completed, 3, read.MaxIterations, read.CompletionPromise,
            read.LastPromptSent, read.LastAiResponse, changed.Outcome, read.CreatedAt, _at.AddMinutes(1),
            _at.AddMinutes(2), _at.AddMinutes(3), "why", read.CreatedBy, read.AssignedTo, 0, []);
        loaded.Should().BeEquivalentTo(expected, Members);
    }

    /// <summary>
    ///     Pending and ready requests list by priority, then oldest first, then by id (ruling R92); others are left out
    ///     and the batch size bounds the list. The id tie-breaker has no reliable red: without it the order of tied rows
    ///     is undefined, so the test pins the order it defines.
    /// </summary>
    [Fact]
    public async SystemTask Pending_requests_list_by_priority_then_age_then_id()
    {
        var tied = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            tied.Add(await AddRequestAsync(AnalysisStatus.Ready, priority: 1, createdAt: _at));
        }

        var urgent = await AddRequestAsync(AnalysisStatus.Pending, priority: 0, createdAt: _at.AddHours(1));
        var older = await AddRequestAsync(AnalysisStatus.Pending, priority: 1, createdAt: _at.AddHours(-1));
        await AddRequestAsync(AnalysisStatus.Completed, priority: 0, createdAt: _at);

        var listed = (await Repository().GetPendingAsync(4)).Value;

        listed.Select(r => r.Id).Should().Equal([urgent, older, .. tied.Order().Take(2)]);
    }

    private static Func<EquivalencyOptions<CodeAnalysisRequest>, EquivalencyOptions<CodeAnalysisRequest>> Members =>
        o => o.ComparingByMembers<CodeAnalysisRequest>().ComparingByMembers<AnalysisIteration>()
            .ComparingByMembers<RepositoryReference>().ComparingByMembers<CodeLocation>()
            .ComparingByMembers<AnalysisOutcome>()
            .WithStrictOrderingFor(r => r.Iterations)
            .Excluding((IMemberInfo m) => m.Name == nameof(CodeAnalysisRequest.RowVersion));

    private static CodeAnalysisRequest Expected(CodeAnalysisRequest r, IEnumerable<AnalysisIteration> iterations) =>
        CodeAnalysisRequest.Rehydrate(
            r.Id, r.Title, r.Description, r.Repository, r.Location, r.Type, r.Priority, r.Requirements,
            r.ReferencedDocuments, r.ExternalReferences, r.LocalWorkTreePath, r.FeatureBranchName, r.Status,
            r.CurrentIteration, r.MaxIterations, r.CompletionPromise, r.LastPromptSent, r.LastAiResponse, r.Outcome,
            r.CreatedAt, r.StartedAt, r.CompletedAt, r.CancelledAt, r.FailureReason, r.CreatedBy, r.AssignedTo, 0,
            iterations);

    private static AnalysisIteration Iteration(Guid requestId, int number, string prompt) =>
        AnalysisIteration.Rehydrate(
            Guid.NewGuid(), requestId, number, "branch", "sha", prompt, "response " + number, true, false,
            """{"errors": []}""", 10 * number, 20 * number, "model", _at.AddSeconds(number));

    private static AnalysisStatus StatusFor(string method) =>
        string.Equals(method, "UpdateStatus", StringComparison.Ordinal)
            ? AnalysisStatus.Ready
            : AnalysisStatus.AnalysisInProgress;

    private Task<Result> Independent(string method, Guid id) =>
        string.Equals(method, "UpdateWorkTreePath", StringComparison.Ordinal)
            ? Repository().UpdateWorkTreePathAsync(id, " /tmp/new-tree ")
            : Repository().UpdateLastPromptAsync(id, " new prompt ", " new response ");

    private Task<Result> Guarded(string method, Guid id) => method switch
    {
        "UpdateStatus" => Repository().UpdateStatusAsync(id, AnalysisStatus.AnalysisInProgress),
        "UpdateIteration" => Repository().UpdateIterationAsync(id, 1, "prompt", "response"),
        "RecordValidation" => Repository().RecordValidationAsync(id, """{"ok": false}""", true),
        "Complete" => Repository().CompleteAsync(id, "https://github.com/org/repo/pull/2", "sha2"),
        "Fail" => Repository().FailAsync(id, "second writer"),
        "Cancel" => Repository().CancelAsync(id),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    private async Task<Guid> AddRequestAsync(AnalysisStatus status, int priority = 0, DateTime? createdAt = null)
    {
        var request = CodeAnalysisRequest.Rehydrate(
            Guid.NewGuid(), "Title", "Description",
            RepositoryReference.Rehydrate("https://github.com/org/repo", "main", null, RepositoryPlatform.GitHub),
            CodeLocation.Rehydrate("src/a.cs", null, null), AnalysisType.Refactor, priority, "[]", null, null,
            "/tmp/old-tree", "feature/x", status, 0, 10, null, "old prompt", "old response", AnalysisOutcome.Empty(),
            createdAt ?? _at, null, null, null, null, null, null, 0, []);
        (await Repository().CreateAsync(request)).IsSuccess.Should().BeTrue();
        return request.Id;
    }

    /// <summary>
    ///     Runs <paramref name="sql"/> on another connection in an open transaction, starts <paramref name="operation"/>,
    ///     waits until the operation's write is blocked by that transaction, so it has loaded the committed row, then
    ///     commits and returns the operation's result.
    /// </summary>
    private async Task<Result> WhileAnotherWriteHoldsTheRowAsync(string sql, Guid id, Func<Task<Result>> operation)
    {
        await using var writer = new NpgsqlConnection(fixture.ConnectionString);
        await writer.OpenAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(sql, writer, tx))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("at", _at);
            (await command.ExecuteNonQueryAsync()).Should().Be(1, "the other write changes the request's row");
        }

        var pending = SystemTask.Run(operation);
        await WaitUntilBlockedByAsync(writer.ProcessID, pending);
        await tx.CommitAsync();
        return await pending;
    }

    private async SystemTask WaitUntilBlockedByAsync(int blockerPid, SystemTask pending)
    {
        await using var probe = new NpgsqlConnection(fixture.ConnectionString);
        await probe.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE @pid = ANY(pg_blocking_pids(pid))", probe);
        command.Parameters.AddWithValue("pid", blockerPid);
        var clock = Stopwatch.StartNew();
        while (!pending.IsCompleted)
        {
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the operation should reach its write");
            await SystemTask.Delay(10);
        }
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

    /// <summary>Records each entry's level and exception.</summary>
    private sealed class CapturingLogger : ILogger<CodeAnalysisRepository>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }
}
