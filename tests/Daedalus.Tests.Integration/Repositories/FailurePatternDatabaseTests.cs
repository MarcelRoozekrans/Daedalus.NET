using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     Phase 3.5: the failure pattern search on ZeroAlloc.ORM matches error text case-insensitively, pairs each error
///     with the next successful iteration of the same task, cuts the resolution at 500 characters, and reads a row
///     whatever <c>Create</c> would say of it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FailurePatternDatabaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);
    private Guid _projectId;

    public async SystemTask InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _projectId = Guid.NewGuid();
        await ExecuteAsync(
            """INSERT INTO "Projects" ("Id", "ProjectName", "Description", "CreatedAt", "Version") VALUES (@id, 'P', 'D', @at, '1.0')""",
            ("id", _projectId), ("at", _at));
    }

    public SystemTask DisposeAsync() => SystemTask.CompletedTask;

    private FailurePatternDatabase Database() => new(fixture.Db, NullLogger<FailurePatternDatabase>.Instance);

    /// <summary>
    ///     An error matched in another case is paired with the next iteration of its task that has no error and a
    ///     response, skipping a failed iteration and an empty response between them; the resolution is cut at 500
    ///     characters and every field comes back. Red, shown once: drop the non-empty condition from the resolution
    ///     query, and the comparison names <c>ResolutionIteration</c> and <c>Resolution</c>.
    /// </summary>
    [Fact]
    public async SystemTask Stored_pattern_loads_back_whole()
    {
        var taskId = await AddTaskAsync("TASK-001");
        await AddExecutionAsync(taskId, 1, "response 1", "Null Reference in Foo", _at);
        await AddExecutionAsync(taskId, 2, "second error", "another failure", _at.AddMinutes(1));
        await AddExecutionAsync(taskId, 3, "", null, _at.AddMinutes(2));
        await AddExecutionAsync(taskId, 4, new string('x', 700), null, _at.AddMinutes(3));
        await AddExecutionAsync(taskId, 5, "later fix", null, _at.AddMinutes(4));

        var result = await Database().SearchByErrorAsync("null reference", 5, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var expected = new FailurePatternRecord
        {
            ErrorText = "Null Reference in Foo",
            Resolution = new string('x', 500),
            SourceTaskId = taskId,
            ErrorIteration = 1,
            ResolutionIteration = 4,
            ObservedAt = _at
        };
        result.Value.Should().ContainSingle().Which
            .Should().BeEquivalentTo(expected, o => o.ComparingByMembers<FailurePatternRecord>());
        result.Value[0].ObservedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    /// <summary>
    ///     An error with no later successful iteration yields no pattern, the newest errors come first, and the result
    ///     stops at <c>maxResults</c>. Red: order the error query ascending and the newest-first assertion fails.
    /// </summary>
    [Fact]
    public async SystemTask Unresolved_errors_are_skipped_the_newest_come_first_and_the_count_is_capped()
    {
        var unresolved = await AddTaskAsync("TASK-U");
        await AddExecutionAsync(unresolved, 1, "r", "timeout reading", _at.AddHours(5));
        var older = await AddTaskAsync("TASK-A");
        await AddExecutionAsync(older, 1, "r", "timeout reading", _at);
        await AddExecutionAsync(older, 2, "fixed a", null, _at.AddMinutes(1));
        var newer = await AddTaskAsync("TASK-B");
        await AddExecutionAsync(newer, 1, "r", "timeout reading", _at.AddHours(1));
        await AddExecutionAsync(newer, 2, "fixed b", null, _at.AddHours(1).AddMinutes(1));

        var all = (await Database().SearchByErrorAsync("timeout", 5, CancellationToken.None)).Value;
        var capped = (await Database().SearchByErrorAsync("timeout", 1, CancellationToken.None)).Value;

        all.Select(p => p.SourceTaskId).Should().Equal(newer, older);
        capped.Should().ContainSingle().Which.SourceTaskId.Should().Be(newer);
    }

    /// <summary>
    ///     The search looks at the newest <c>2 * maxResults</c> errors only: two unresolved errors newer than a resolved
    ///     one fill the window of <c>maxResults = 1</c>, so nothing is found. Red, shown once: drop <c>Take(limit)</c>
    ///     from the error query and the older resolved error is found.
    /// </summary>
    [Fact]
    public async SystemTask The_error_window_is_twice_the_result_count()
    {
        var resolved = await AddTaskAsync("TASK-R1");
        await AddExecutionAsync(resolved, 1, "r", "window failure", _at);
        await AddExecutionAsync(resolved, 2, "fixed", null, _at.AddMinutes(1));
        foreach (var (identifier, hours) in new[] { ("TASK-N1", 2), ("TASK-N2", 3) })
        {
            var unresolved = await AddTaskAsync(identifier);
            await AddExecutionAsync(unresolved, 1, "r", "window failure", _at.AddHours(hours));
        }

        var result = await Database().SearchByErrorAsync("window failure", 1, CancellationToken.None);

        result.Value.Should().BeEmpty();
    }

    /// <summary>
    ///     The result stops at <c>maxResults</c> though the window holds more resolved errors. Red, shown once: drop
    ///     the <c>break</c> from the search loop and both come back.
    /// </summary>
    [Fact]
    public async SystemTask The_result_stops_at_the_count_though_the_window_holds_more()
    {
        foreach (var (identifier, hours) in new[] { ("TASK-S1", 1), ("TASK-S2", 2) })
        {
            var taskId = await AddTaskAsync(identifier);
            await AddExecutionAsync(taskId, 1, "r", "stop failure", _at.AddHours(hours));
            await AddExecutionAsync(taskId, 2, "fixed", null, _at.AddHours(hours).AddMinutes(1));
        }

        var result = await Database().SearchByErrorAsync("stop failure", 1, CancellationToken.None);

        result.Value.Should().ContainSingle();
    }

    /// <summary>
    ///     Today's ILIKE passes <c>%</c> and <c>_</c> of the search text through as wildcards. A blank text finds
    ///     nothing.
    /// </summary>
    [Fact]
    public async SystemTask Wildcards_in_the_search_text_pass_through_and_a_blank_text_finds_nothing()
    {
        var taskId = await AddTaskAsync("TASK-W");
        await AddExecutionAsync(taskId, 1, "r", "CS1061 missing member", _at);
        await AddExecutionAsync(taskId, 2, "fix", null, _at.AddMinutes(1));

        var wild = await Database().SearchByErrorAsync("CS%member", 5, CancellationToken.None);
        var single = await Database().SearchByErrorAsync("CS1_61 missing", 5, CancellationToken.None);
        var none = await Database().SearchByErrorAsync("CS1_1 missing", 5, CancellationToken.None);
        var blank = await Database().SearchByErrorAsync("  ", 5, CancellationToken.None);

        wild.Value.Should().ContainSingle();
        single.Value.Should().ContainSingle("_ stands for one character");
        none.Value.Should().BeEmpty("_ stands for exactly one character");
        blank.Value.Should().BeEmpty();
    }

    /// <summary>
    ///     The prompt search extracts keywords, searches each, and de-duplicates by task and error iteration.
    /// </summary>
    [Fact]
    public async SystemTask The_prompt_search_finds_patterns_for_its_keywords_without_duplicates()
    {
        var taskId = await AddTaskAsync("TASK-P");
        await AddExecutionAsync(taskId, 1, "r", "CS1061 in migration step", _at);
        await AddExecutionAsync(taskId, 2, "fix", null, _at.AddMinutes(1));

        var result = await Database().SearchByPromptContextAsync(
            "Fix CS1061 while adding a migration", 5, CancellationToken.None);

        result.Value.Should().ContainSingle().Which.ErrorIteration.Should().Be(1);
    }

    /// <summary>
    ///     Ruling C12: an iteration with an empty response is not a resolution; the next one with text is. Red, shown
    ///     once: drop <c>length("LlmResponse") &gt; 0</c> from the resolution query and the resolution iteration is 2.
    /// </summary>
    [Fact]
    public async SystemTask An_empty_response_is_not_a_resolution()
    {
        var taskId = await AddTaskAsync("TASK-E");
        await AddExecutionAsync(taskId, 1, "r", "disk full", _at);
        await AddExecutionAsync(taskId, 2, "", null, _at.AddMinutes(1));
        await AddExecutionAsync(taskId, 3, "freed space", null, _at.AddMinutes(2));

        var result = await Database().SearchByErrorAsync("disk", 5, CancellationToken.None);

        var pattern = result.Value.Should().ContainSingle().Subject;
        pattern.ResolutionIteration.Should().Be(3);
        pattern.Resolution.Should().Be("freed space");
    }

    /// <summary>
    ///     Rehydrate never validates (R18, plan Review Focus 1): execution rows nothing in the domain would create, an
    ///     iteration number of 0 and negative token counts, still load as stored.
    /// </summary>
    [Fact]
    public async SystemTask A_row_with_values_nothing_would_create_still_loads()
    {
        var taskId = await AddTaskAsync("TASK-R");
        await AddExecutionAsync(taskId, 0, "r", "odd failure", _at, inputTokens: -1, outputTokens: -2);
        await AddExecutionAsync(taskId, 1, "fixed", null, _at.AddMinutes(1), inputTokens: -3, outputTokens: -4);

        var result = await Database().SearchByErrorAsync("odd", 5, CancellationToken.None);

        var pattern = result.Value.Should().ContainSingle().Subject;
        pattern.ErrorIteration.Should().Be(0);
        pattern.ResolutionIteration.Should().Be(1);
    }

    private async Task<Guid> AddTaskAsync(string identifier)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "Tasks"
                ("Id", "TaskId", "ProjectId", "Title", "Description", "Priority", "Status", "Phase", "ParallelGroup",
                 "EstimatedComplexity", "Prompt", "CompletionPromise", "MaxIterations", "IterationCount", "CreatedAt",
                 "Learnings", "Dependencies", "FilesToModify")
            VALUES (@id, @taskId, @projectId, 'Title', 'D', 0, 0, 'Backend', 1, 0, 'P', 'done', 5, 0, @at, '',
                    ARRAY[]::text[], ARRAY[]::text[])
            """,
            ("id", id), ("taskId", identifier), ("projectId", _projectId), ("at", _at));
        return id;
    }

    private SystemTask AddExecutionAsync(
        Guid taskId, int iteration, string response, string? error, DateTime at, int inputTokens = 1,
        int outputTokens = 2) =>
        ExecuteAsync(
            """
            INSERT INTO "TaskExecutions"
                ("Id", "TaskId", "SessionId", "IterationNumber", "Prompt", "LlmResponse", "CompletionPromiseFound",
                 "ExecutedAt", "ExecutionDuration", "Error", "InputTokens", "ModelId", "OutputTokens")
            VALUES (@id, @taskId, @sessionId, @iteration, 'p', @response, false, @at, interval '1 second', @error,
                    @input, NULL, @output)
            """,
            ("id", Guid.NewGuid()), ("taskId", taskId), ("sessionId", Guid.NewGuid()), ("iteration", iteration),
            ("response", response), ("at", at), ("error", (object?)error ?? DBNull.Value), ("input", inputTokens),
            ("output", outputTokens));

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
