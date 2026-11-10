using System.Data.Async;
using System.Text.Json.Nodes;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.Tasks;
using Daedalus.Tests.Integration.Builders;
using Daedalus.Tests.Integration.Fixtures;
using Daedalus.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using DomainTask = Daedalus.Domain.Entities.Task;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Persistence;

/// <summary>
///     Phase 3.5, Task 1: a spike that proves ZeroAlloc.ORM 2.13.0 with <c>ZeroAlloc.ORM.Linq</c> handles each column
///     shape the store ports rely on, over the real schema EF builds today. <see cref="CapabilitySql"/> and its maps
///     are throwaway: the store tasks write their own in Infrastructure. A failure here is an ORM gap for the owner,
///     not something to work around.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OrmCapabilityTests(PostgresFixture fixture) : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = null!;
    private DaedalusDb _db = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        _db = new DaedalusDb(_dataSource);
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task Text_array_column_reads_and_writes()
    {
        await using var connection = await _db.OpenAsync(CancellationToken.None);
        var sql = new CapabilitySql(connection);

        await sql.InsertSkill("spike-skill", ["alpha", "beta"], CancellationToken.None);
        (await sql.SkillTags("spike-skill", CancellationToken.None))!.Tags.Should().Equal("alpha", "beta");

        await sql.RetagSkill("spike-skill", ["gamma"], CancellationToken.None);
        (await sql.SkillTags("spike-skill", CancellationToken.None))!.Tags.Should().Equal("gamma");
    }

    [Fact]
    public async Task Array_parameter_filters_with_any()
    {
        var tasks = await SeedTasksAsync(3);

        await using var connection = await _db.OpenAsync(CancellationToken.None);
        Guid[] wanted = [tasks[0].Id, tasks[2].Id];

        var found = await new CapabilitySql(connection).TaskIdsIn(wanted, CancellationToken.None);

        found.Should().BeEquivalentTo(wanted);
    }

    [Fact]
    public async Task Jsonb_string_inserts_and_reads()
    {
        var session = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), "owner", DateTime.UtcNow).Value;
        await StoreSeed.AddSessionAsync(_db, session);

        const string Content = """{"role":"user","contents":[{"text":"hi"}]}""";
        await using var connection = await _db.OpenAsync(CancellationToken.None);
        var sql = new CapabilitySql(connection);

        await sql.InsertMessage(Guid.NewGuid(), session.Id, Content, DateTime.UtcNow, CancellationToken.None);
        var read = await sql.MessageContents(session.Id, CancellationToken.None);

        read.Should().ContainSingle();
        JsonNode.DeepEquals(JsonNode.Parse(read[0]), JsonNode.Parse(Content)).Should().BeTrue(read[0]);
    }

    [Fact]
    public async Task Xmin_reads_and_guards()
    {
        var task = (await SeedTasksAsync(1))[0];

        await using var connection = await _db.OpenAsync(CancellationToken.None);
        var sql = new CapabilitySql(connection);
        var read = (await sql.TaskVersion(task.Id, CancellationToken.None))!;
        read.RowVersion.Should().Be(task.RowVersion, "the seed read the task back with its xmin, and nothing wrote since");

        await sql.RetitleTask(task.Id, read.RowVersion, "first", CancellationToken.None);
        var stale = () => sql.RetitleTask(task.Id, read.RowVersion, "second", CancellationToken.None);

        await stale.Should().ThrowAsync<ZeroAllocOrmConcurrencyException>();
        var after = (await sql.TaskVersion(task.Id, CancellationToken.None))!;
        after.Title.Should().Be("first");
        after.RowVersion.Should().NotBe(read.RowVersion);
    }

    [Fact]
    public async Task Enum_stored_as_string_compares()
    {
        await ScheduleTables.InsertAsync(
            fixture, NewRun("from-config", ScheduleOrigin.Config), NewRun("from-agent", ScheduleOrigin.Agent));

        await using var connection = await _db.OpenAsync(CancellationToken.None);
        var sql = new CapabilitySql(connection);

        (await sql.RunsWithOrigin(ScheduleOrigin.Agent, CancellationToken.None))
            .Should().Equal(new RunOrigin("from-agent", ScheduleOrigin.Agent));
        (await sql.ConfigRunNames(CancellationToken.None)).Should().Equal("from-config");
    }

    [Fact]
    public async Task Owned_columns_read()
    {
        const string FullUrl = "https://github.com/example/repo";
        const string BareUrl = "https://github.com/example/bare";
        var full = CodeAnalysisRequest.Create(
            Guid.NewGuid(), "Spike", "Owned columns", FullUrl, 3,
            requirements: "[]", repositoryBranch: "main", commitSha: "abc123").Value;
        var bare = CodeAnalysisRequest.Create(Guid.NewGuid(), "Spike", "Null owned columns", BareUrl, 3, requirements: "[]").Value;
        // Seeded through the ported repository since Task 15 deleted the EF mapping (ruling R96).
        var repository = new CodeAnalysisRepository(_db, NullLogger<CodeAnalysisRepository>.Instance);
        (await repository.CreateAsync(full)).IsSuccess.Should().BeTrue();
        (await repository.CreateAsync(bare)).IsSuccess.Should().BeTrue();

        await using var connection = await _db.OpenAsync(CancellationToken.None);
        var sql = new CapabilitySql(connection);

        (await sql.RequestByUrl(FullUrl, CancellationToken.None))
            .Should().Be(new RequestRepository(full.Id, FullUrl, "main", "abc123", full.Platform));
        (await sql.RequestByUrl(BareUrl, CancellationToken.None))
            .Should().Be(new RequestRepository(bare.Id, BareUrl, null, null, bare.Platform));
    }

    private async Task<List<DomainTask>> SeedTasksAsync(int count)
    {
        var project = Project.Create(Guid.NewGuid(), "Spike", "Owns the spike's tasks").Value;
        var tasks = Enumerable.Range(0, count).Select(_ => new TaskTestBuilder().WithProjectId(project.Id).Build()).ToList();
        await StoreSeed.AddAsync(_db, [project], tasks);

        // Read back, so each task carries the xmin its insert gave it, as EF read it back after SaveChangesAsync.
        await using var connection = await _db.OpenAsync(CancellationToken.None);
        var sql = new TaskSql(connection);
        var stored = new List<DomainTask>(tasks.Count);
        foreach (var task in tasks)
        {
            stored.Add((await sql.FindAsync(task.Id, CancellationToken.None))!);
        }

        return stored;
    }

    private static ScheduledRun NewRun(string name, ScheduleOrigin origin) =>
        ScheduledRun.Create(
            name, "0 7 * * *", "RepoDigestSaga", "telegram", "123456", "schedule:" + name, ["reader"], origin,
            new DateTime(2026, 11, 5, 7, 0, 0, DateTimeKind.Utc)).Value;
}

internal sealed record SkillTagsRow(string Name, IReadOnlyList<string> Tags);

internal sealed record TaskVersionRow(Guid Id, string Title, uint RowVersion);

internal sealed record RunOrigin(string Name, ScheduleOrigin Origin);

internal sealed record RequestRepository(
    Guid Id, string Url, string? Branch, string? CommitSha, RepositoryPlatform Platform);

[TableMap(typeof(Skill), "Skills")]
[ColumnMap(nameof(Skill.Id), Column = "Name")]
[ColumnMap(nameof(Skill.Tags))]
internal static class CapabilitySkillMap;

[TableMap(typeof(AgentMessage), "AgentMessages")]
[ColumnMap(nameof(AgentMessage.ContentJson), Json = true)]
internal static class CapabilityAgentMessageMap;

[TableMap(typeof(DomainTask), "Tasks")]
[ColumnMap(nameof(DomainTask.RowVersion), Column = "xmin")]
internal static class CapabilityTaskMap;

[TableMap(typeof(ScheduledRun), "ScheduledRuns")]
[ColumnMap(nameof(ScheduledRun.Origin), EnumAsString = true)]
internal static class CapabilityScheduledRunMap;

[TableMap(typeof(CodeAnalysisRequest), "CodeAnalysisRequests")]
[OwnedMap(nameof(CodeAnalysisRequest.Repository))]
[ColumnMap("Repository.Url", Column = "RepositoryUrl")]
[ColumnMap("Repository.Branch", Column = "RepositoryBranch")]
[ColumnMap("Repository.CommitSha", Column = "CommitSha")]
[ColumnMap("Repository.Platform", Column = "Platform")]
internal static class CapabilityCodeAnalysisRequestMap;

/// <summary>The spike's queries, one per capability; see <see cref="OrmCapabilityTests"/>.</summary>
[Dialect(SqlDialect.PostgreSql)]
internal sealed partial class CapabilitySql(IAsyncDbConnection connection)
{
    [Command("""
        INSERT INTO "Skills" ("Name", "Description", "Body", "Tags", "SourcePath", "ContentHash", "IsActive", "UpdatedAt")
        VALUES (@name, 'spike', 'spike', @tags, 'spike.md', 'hash', TRUE, now())
        """)]
    public partial Task<int> InsertSkill(string name, string[] tags, CancellationToken ct);

    [LinqQuery(nameof(SkillTagsQuery))]
    public partial Task<SkillTagsRow?> SkillTags(string name, CancellationToken ct);

    [LinqCommand(nameof(RetagSkillQuery))]
    public partial Task<int> RetagSkill(string name, string[] tags, CancellationToken ct);

    [LinqQuery(nameof(TaskIdsInQuery))]
    public partial Task<List<Guid>> TaskIdsIn(Guid[] ids, CancellationToken ct);

    [Command("""
        INSERT INTO "AgentMessages"
            ("Id", "SessionId", "Sequence", "Role", "ContentJson", "InputTokens", "OutputTokens", "ModelId", "CreatedAt")
        VALUES (@id, @sessionId, 0, 'user', @contentJson, NULL, NULL, NULL, @createdAt)
        """)]
    public partial Task<int> InsertMessage(
        Guid id, Guid sessionId, [Json] string contentJson, DateTime createdAt, CancellationToken ct);

    [LinqQuery(nameof(MessageContentsQuery))]
    public partial Task<List<string>> MessageContents(Guid sessionId, CancellationToken ct);

    [LinqQuery(nameof(TaskVersionQuery))]
    public partial Task<TaskVersionRow?> TaskVersion(Guid id, CancellationToken ct);

    [LinqCommand(nameof(RetitleTaskQuery), ThrowOnNoRows = true)]
    public partial Task RetitleTask(Guid id, uint expected, string title, CancellationToken ct);

    [LinqQuery(nameof(RunsWithOriginQuery))]
    public partial Task<List<RunOrigin>> RunsWithOrigin(ScheduleOrigin origin, CancellationToken ct);

    [LinqQuery(nameof(ConfigRunNamesQuery))]
    public partial Task<List<string>> ConfigRunNames(CancellationToken ct);

    [LinqQuery(nameof(RequestByUrlQuery))]
    public partial Task<RequestRepository?> RequestByUrl(string url, CancellationToken ct);

    private static IQuery<SkillTagsRow> SkillTagsQuery(string name) =>
        Sql.Table<Skill>().Where(s => string.Equals(s.Id, name, StringComparison.Ordinal)).Select(s => new SkillTagsRow(s.Id, s.Tags));

    private static int RetagSkillQuery(string name, string[] tags) =>
        Sql.Table<Skill>().Where(s => string.Equals(s.Id, name, StringComparison.Ordinal)).ExecuteUpdate(set => set.SetProperty(s => s.Tags, tags));

    private static IQuery<Guid> TaskIdsInQuery(Guid[] ids) =>
        Sql.Table<DomainTask>().Where(t => ids.Contains(t.Id)).Select(t => t.Id);

    private static IQuery<string> MessageContentsQuery(Guid sessionId) =>
        Sql.Table<AgentMessage>().Where(m => m.SessionId == sessionId).Select(m => m.ContentJson);

    private static IQuery<TaskVersionRow> TaskVersionQuery(Guid id) =>
        Sql.Table<DomainTask>().Where(t => t.Id == id).Select(t => new TaskVersionRow(t.Id, t.Title, t.RowVersion));

    private static int RetitleTaskQuery(Guid id, uint expected, string title) =>
        Sql.Table<DomainTask>()
            .Where(t => t.Id == id && t.RowVersion == expected)
            .ExecuteUpdate(set => set.SetProperty(t => t.Title, title));

    private static IQuery<RunOrigin> RunsWithOriginQuery(ScheduleOrigin origin) =>
        Sql.Table<ScheduledRun>().Where(r => r.Origin == origin).OrderBy(r => r.Name).Select(r => new RunOrigin(r.Name, r.Origin));

    private static IQuery<string> ConfigRunNamesQuery() =>
        Sql.Table<ScheduledRun>().Where(r => r.Origin == ScheduleOrigin.Config).OrderBy(r => r.Name).Select(r => r.Name);

    private static IQuery<RequestRepository> RequestByUrlQuery(string url) =>
        Sql.Table<CodeAnalysisRequest>()
            .Where(r => string.Equals(r.Repository.Url, url, StringComparison.Ordinal))
            .Select(r => new RequestRepository(
                r.Id, r.Repository.Url, r.Repository.Branch, r.Repository.CommitSha, r.Repository.Platform));
}
