using System.Reflection;
using System.Security.Claims;
using Daedalus.Agents;
using Daedalus.Agents.Memory;
using Daedalus.Api.Endpoints;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Thalos;
using Thalos.Memory;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     <see cref="AgentMemoriesEndpoints"/> over the real <see cref="PostgresMemoryStore"/> (fixture database) and a fake
///     <see cref="IMemoryService"/>: which owners a caller may read, that foreign ids answer 404 rather than 403, and that
///     forgetting a shared-owner memory needs the developer policy. The query parameters are bound through the handlers'
///     own <see cref="ApiQuery{T}"/> parameters, as the request delegate binds them.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AgentMemoriesEndpointsIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Shared = "daedalus";
    private const string Category = "Daedalus.Api.Endpoints.AgentMemoriesEndpoints";
    private static readonly DateTimeOffset T0 = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddDaedalusEndpoints().BuildServiceProvider();
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();
    private readonly MemoryConfig _config = new() { SharedOwnerId = Shared };
    private readonly FakeTimeProvider _clock = new(T0);
    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private PostgresMemoryStore _store = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _store = new PostgresMemoryStore(new FixtureDbContextFactory(fixture), _clock);
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(_logger);
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task List_queries_own_and_shared_owners_and_returns_a_page()
    {
        var record = await SeedAsync("alice", "one");
        MemoryQuery? seen = null;
        _memory.ListAsync(Arg.Any<MemoryQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seen = call.Arg<MemoryQuery>();
                return ZeroAlloc.Results.Result<MemoryPage, AgentError>.Success(new MemoryPage([record], 2, 100, 7));
            });

        var result = await ListAsync("alice", kind: "Learning", pageSize: "500", page: "2");

        seen.Should().NotBeNull();
        // Red: OwnerIds = VisibleOwners(caller, config).Take(1).ToList() in List; the shared owner is lost.
        seen!.OwnerIds.Should().Equal("alice", Shared);
        // Red: Kinds = memoryKind is null ? null : [] in the query List builds; the kind filter is lost.
        seen.Kinds.Should().ContainSingle().Which.Value.Should().Be("learning", "kinds are lower-case identifiers");
        // Red: Math.Clamp(pageSize.ValueOr(DefaultPageSize), 1, MaxPageSize - 50) in List; the bound is no longer the Thalos one.
        seen.PageSize.Should().Be(MemoryQuery.MaxPageSize, "the endpoint clamps to the Thalos page bound");
        // Red: Page = 1 in place of Math.Max(1, page.ValueOr(1)) in List.
        seen.Page.Should().Be(2);
        // Red: IncludeArchived = true in the query List builds.
        seen.IncludeArchived.Should().BeFalse();
        seen.Tags.Should().BeNull();

        // Red: new PagedResultDto<MemoryDto>(items, 0, 1, 20) in the Ok of List; Total, Page and PageSize are lost.
        var page = result.Result.Should().BeOfType<Ok<PagedResultDto<MemoryDto>>>().Subject.Value!;
        page.Should().BeEquivalentTo(new { Total = 7, Page = 2, PageSize = 100 });
        page.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = record.Id.ToString(), OwnerId = "alice", Text = "one", IsShared = false });
    }

    [Fact]
    public async Task List_of_the_shared_owner_itself_does_not_repeat_the_owner_and_flags_shared()
    {
        var record = await SeedAsync(Shared, "project knowledge");
        MemoryQuery? seen = null;
        _memory.ListAsync(Arg.Any<MemoryQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seen = call.Arg<MemoryQuery>();
                return ZeroAlloc.Results.Result<MemoryPage, AgentError>.Success(new MemoryPage([record], 1, 20, 1));
            });

        var result = await ListAsync(Shared, tag: " Database ", includeArchived: "true");

        // Red: add config.SharedOwnerId unconditionally in VisibleOwners; the owner is listed twice.
        seen!.OwnerIds.Should().Equal(Shared);
        // Red: Tags = [tag] without Trim in List.
        seen.Tags.Should().Equal("Database");
        // Red: IncludeArchived = false in the query List builds.
        seen.IncludeArchived.Should().BeTrue();
        // Red: pass string.Empty as the shared owner id to AgentDtoMapper.ToDto in List; IsShared is lost.
        var page = result.Result.Should().BeOfType<Ok<PagedResultDto<MemoryDto>>>().Subject.Value!;
        page.Items.Should().ContainSingle().Which.IsShared.Should().BeTrue();
    }

    [Fact]
    public async Task List_maps_a_service_failure_to_its_status()
    {
        _memory.ListAsync(Arg.Any<MemoryQuery>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<MemoryPage, AgentError>.Failure(AgentError.MemoryStoreFailed("the store is unhappy")));

        var result = await ListAsync("alice");

        // Red: return ApiProblem.Of(context, 500) in place of result.Error.ToProblem(context) in List.
        AssertProblem(result.Result, StatusCodes.Status502BadGateway, "MemoryStoreFailed");
    }

    [Theory]
    [InlineData("not-an-agent", null)]
    [InlineData(null, "Not a Kind!")]
    public async Task List_rejects_an_unparsable_filter_and_never_calls_the_service(string? agentId, string? kind)
    {
        var result = await ListAsync("alice", agentId: agentId, kind: kind);

        // Red: drop the TryScope refusal in List, then separately the MemoryKind.TryParse refusal; the service is called.
        AssertProblem(result.Result, StatusCodes.Status400BadRequest, "Validation");
        await _memory.DidNotReceiveWithAnyArgs().ListAsync(null!, default);
    }

    [Fact]
    public async Task Get_own_and_shared_are_visible_foreign_is_404()
    {
        var mine = await SeedAsync("alice", "mine");
        var shared = await SeedAsync(Shared, "the project's");
        var foreign = await SeedAsync("bob", "bob's");

        // Red: return TypedResults.Ok<MemoryDto>(null!) in place of the mapped record in Get.
        var own = await GetAsync("alice", mine.Id.ToString());
        own.Result.Should().BeOfType<Ok<MemoryDto>>().Subject.Value
            .Should().BeEquivalentTo(new { Id = mine.Id.ToString(), Text = "mine", IsShared = false });

        var project = await GetAsync("alice", shared.Id.ToString());
        project.Result.Should().BeOfType<Ok<MemoryDto>>().Subject.Value
            .Should().BeEquivalentTo(new { Id = shared.Id.ToString(), Text = "the project's", IsShared = true });

        // Red: answer 403 in place of MemoryNotFound for a foreign record in Get; ids can be probed.
        AssertProblem((await GetAsync("alice", foreign.Id.ToString())).Result, StatusCodes.Status404NotFound, "MemoryNotFound");
        AssertProblem((await GetAsync("alice", MemoryId.New().ToString())).Result, StatusCodes.Status404NotFound, "MemoryNotFound");
    }

    [Fact]
    public async Task List_and_Get_hide_memories_pinned_to_another_agent_exactly_like_the_memory_tools()
    {
        var agent = AgentId.New();
        var ownWide = await SeedAsync("alice", "owner-wide");
        var ownPinned = await SeedAsync("alice", "pinned to the session's agent", agent);
        var otherPinned = await SeedAsync("alice", "pinned to another agent", AgentId.New());
        var sharedWide = await SeedAsync(Shared, "shared owner-wide");
        var sharedPinned = await SeedAsync(Shared, "shared but pinned", agent);
        MemoryPage page = new([ownWide, ownPinned, otherPinned, sharedWide, sharedPinned], 1, 20, 5);
        MemoryQuery? seen = null;
        _memory.ListAsync(Arg.Any<MemoryQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seen = call.Arg<MemoryQuery>();
                return ZeroAlloc.Results.Result<MemoryPage, AgentError>.Success(page);
            });

        // Without an agent context only the owner-wide records are visible…
        // Red: .Where(r => scope.Includes(r.OwnerId, r.AgentId) || r.OwnerId.Length > 0) in List; every record is returned.
        var anonymousAgent = await ListAsync("alice");
        Texts(anonymousAgent).Should().Equal("owner-wide", "shared owner-wide");

        // …with one, that agent's own pinned records join them; the shared owner never grants pinned records.
        // Red: build the scope without the agent in TryScope; the pinned record stays hidden.
        var withAgent = await ListAsync("alice", agentId: agent.ToString());
        Texts(withAgent).Should().Equal("owner-wide", "pinned to the session's agent", "shared owner-wide");
        // Red: AgentId = scope.AgentId in the query List builds; the query is pinned to one agent.
        seen!.AgentId.Should().BeNull("that member pins the query to one agent; visibility is post-filtered like memory__list");

        // Get applies the same rule, and answers 404 (never 403) for what it hides.
        // Red: drop the scope.Includes check in Get; the pinned record is served without an agent context.
        AssertProblem((await GetAsync("alice", ownPinned.Id.ToString())).Result, StatusCodes.Status404NotFound, "MemoryNotFound");
        (await GetAsync("alice", ownPinned.Id.ToString(), agent.ToString())).Result.Should().BeOfType<Ok<MemoryDto>>();
        AssertProblem((await GetAsync("alice", sharedPinned.Id.ToString(), agent.ToString())).Result, StatusCodes.Status404NotFound, "MemoryNotFound");
        AssertProblem((await GetAsync("alice", otherPinned.Id.ToString(), agent.ToString())).Result, StatusCodes.Status404NotFound, "MemoryNotFound");
    }

    [Fact]
    public async Task Get_of_an_archived_memory_is_404()
    {
        var record = await SeedAsync("alice", "forgotten");
        var archived = await _store.UpdateAsync(record.Id, new MemoryUpdate { IsArchived = true }, CancellationToken.None);
        archived.IsSuccess.Should().BeTrue();

        // The store returns archived records (it is the source of truth); the HTTP surface treats them as gone, and recall
        // drops archived records too, so nothing that needs hydrating is lost.
        // Red: drop record.Value.IsArchived from the visibility check in Get; the archived record is served.
        AssertProblem((await GetAsync("alice", record.Id.ToString())).Result, StatusCodes.Status404NotFound, "MemoryNotFound");
    }

    [Fact]
    public async Task Get_with_an_unparsable_agent_id_is_400()
    {
        var record = await SeedAsync("alice", "mine");

        // Red: ignore a failed TryScope in Get; the record is served.
        AssertProblem((await GetAsync("alice", record.Id.ToString(), "not-an-agent")).Result, StatusCodes.Status400BadRequest, "Validation");
    }

    [Fact]
    public async Task Get_of_a_hidden_memory_logs_the_foreign_access_warning()
    {
        var foreign = await SeedAsync("bob", "bob's");

        await GetAsync("alice", foreign.Id.ToString());

        // Red: drop LogForeignAccess in Get, or change EventId 320 to 323.
        LoggedEvent().Id.Should().Be(320);
        // Red: pass the category "Other" to CreateLogger in Get.
        _loggerFactory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task Forget_own_archives_under_the_callers_scope()
    {
        var mine = await SeedAsync("alice", "mine");
        _memory.ForgetAsync(Arg.Any<MemoryId>(), Arg.Any<MemoryScope>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.UnitResult<AgentError>.Success());

        var result = await ForgetAsync("alice", mine.Id.ToString());

        // Red: return ApiProblem.Of(context, StatusCodes.Status200OK) in place of NoContent in Forget.
        result.Result.Should().BeOfType<NoContent>();
        // Red: pass config.SharedOwnerId as the third MemoryScope argument in the ForgetAsync call.
        await _memory.Received(1).ForgetAsync(
            mine.Id,
            Arg.Is<MemoryScope>(s => s.OwnerId == "alice" && s.AgentId == null && s.SharedOwnerId == null),
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Forget_hard_passes_the_flag_through()
    {
        var mine = await SeedAsync("alice", "mine");
        _memory.ForgetAsync(Arg.Any<MemoryId>(), Arg.Any<MemoryScope>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.UnitResult<AgentError>.Success());

        var result = await ForgetAsync("alice", mine.Id.ToString(), hard: "true");

        result.Result.Should().BeOfType<NoContent>();
        // Red: pass false in place of hard.ValueOr(false) to ForgetAsync.
        await _memory.Received(1).ForgetAsync(mine.Id, Arg.Any<MemoryScope>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Forget_shared_requires_developer_or_admin()
    {
        var shared = await SeedAsync(Shared, "the project's");
        _memory.ForgetAsync(Arg.Any<MemoryId>(), Arg.Any<MemoryScope>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.UnitResult<AgentError>.Success());

        var denied = await ForgetAsync("alice", shared.Id.ToString());
        // Red: skip the Developer.EvaluateAsync check in Forget; the shared memory is forgotten for everyone.
        AssertProblem(denied.Result, StatusCodes.Status403Forbidden, "MemoryForbidden");
        await _memory.DidNotReceiveWithAnyArgs().ForgetAsync(default, default, default, default);

        var developer = await ForgetAsync("carol", shared.Id.ToString(), role: "developer");
        developer.Result.Should().BeOfType<NoContent>();
        var admin = await ForgetAsync("dave", shared.Id.ToString(), role: "admin");
        admin.Result.Should().BeOfType<NoContent>();

        // Red: pass scopeOwner as caller.Id in the shared branch of Forget.
        await _memory.Received(2).ForgetAsync(shared.Id, Arg.Is<MemoryScope>(s => s.OwnerId == Shared), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Forget_foreign_answers_404_and_never_calls_the_service()
    {
        var foreign = await SeedAsync("bob", "bob's");

        var result = await ForgetAsync("alice", foreign.Id.ToString());

        // Red: answer MemoryForbidden in place of MemoryNotFound in the foreign branch of Forget.
        AssertProblem(result.Result, StatusCodes.Status404NotFound, "MemoryNotFound");
        // Red: fall through to ForgetAsync in the foreign branch of Forget.
        await _memory.DidNotReceiveWithAnyArgs().ForgetAsync(default, default, default, default);
        // Red: drop LogForeignAccess in the foreign branch of Forget, or change EventId 320 to 323.
        LoggedEvent().Id.Should().Be(320);
    }

    [Fact]
    public async Task Forget_logs_what_was_forgotten()
    {
        var mine = await SeedAsync("alice", "mine");
        _memory.ForgetAsync(Arg.Any<MemoryId>(), Arg.Any<MemoryScope>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.UnitResult<AgentError>.Success());

        await ForgetAsync("alice", mine.Id.ToString());

        // Red: drop LogForgotten in Forget, or change EventId 321 to 323.
        LoggedEvent().Id.Should().Be(321);
        // Red: pass the category "Other" to CreateLogger in Forget.
        _loggerFactory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task Invalid_id_is_400_on_get_and_forget()
    {
        // Red: append && id.Length == 0 to the MemoryId.TryParse refusal in Get.
        AssertProblem((await GetAsync("alice", "not-an-id")).Result, StatusCodes.Status400BadRequest, "Validation");
        // Red: append && id.Length == 0 to the MemoryId.TryParse refusal in Forget.
        AssertProblem((await ForgetAsync("alice", "not-an-id")).Result, StatusCodes.Status400BadRequest, "Validation");
        await _memory.DidNotReceiveWithAnyArgs().ForgetAsync(default, default, default, default);
    }

    [Fact]
    public async Task Anonymous_is_401_on_every_endpoint()
    {
        // Red: answer StatusCodes.Status403Forbidden in the TryCreate refusal of List.
        AssertStatus((await ListAsync(null)).Result, StatusCodes.Status401Unauthorized);
        // Red: answer StatusCodes.Status403Forbidden in the TryCreate refusal of Get.
        AssertStatus((await GetAsync(null, MemoryId.New().ToString())).Result, StatusCodes.Status401Unauthorized);
        // Red: answer StatusCodes.Status403Forbidden in the TryCreate refusal of Forget.
        AssertStatus((await ForgetAsync(null, MemoryId.New().ToString())).Result, StatusCodes.Status401Unauthorized);

        await _memory.DidNotReceiveWithAnyArgs().ListAsync(null!, default);
        await _memory.DidNotReceiveWithAnyArgs().ForgetAsync(default, default, default, default);
    }

    // ---------- helpers ----------

    private static List<string> Texts(Results<Ok<PagedResultDto<MemoryDto>>, ApiProblemResult> result) =>
        result.Result.Should().BeOfType<Ok<PagedResultDto<MemoryDto>>>().Subject.Value!.Items.Select(m => m.Text).ToList();

    private async Task<MemoryRecord> SeedAsync(string ownerId, string text, AgentId? agentId = null)
    {
        var record = new MemoryRecord
        {
            Id = MemoryId.New(),
            OwnerId = ownerId,
            AgentId = agentId,
            Kind = MemoryKind.Learning,
            Text = text,
            Tags = ["database"],
            Source = "test",
            Importance = 0.5,
            CreatedAt = T0,
            UpdatedAt = T0,
        };

        var created = await _store.CreateAsync(record, CancellationToken.None);
        created.IsSuccess.Should().BeTrue();
        return created.Value;
    }

    private static void AssertStatus(IResult result, int status) =>
        result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(status);

    private static void AssertProblem(IResult result, int status, string code)
    {
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        problem.StatusCode.Should().Be(status);
        problem.ProblemDetails.Status.Should().Be(status);
        problem.ProblemDetails.Title.Should().Be(code);
        problem.ProblemDetails.Extensions.Should().ContainKey("code").WhoseValue.Should().Be(code);
    }

    private EventId LoggedEvent() =>
        _logger.ReceivedCalls().Single(call => string.Equals(call.GetMethodInfo().Name, nameof(ILogger.Log), StringComparison.Ordinal))
            .GetArguments()[1] is EventId id ? id : throw new InvalidOperationException("No EventId argument.");

    private async Task<Results<Ok<PagedResultDto<MemoryDto>>, ApiProblemResult>> ListAsync(
        string? userId, string? agentId = null, string? kind = null, string? tag = null, string? includeArchived = null,
        string? page = null, string? pageSize = null)
    {
        var context = Context(userId, null, ("agentId", agentId), ("kind", kind), ("tag", tag), ("includeArchived", includeArchived),
            ("page", page), ("pageSize", pageSize));
        return await AgentMemoriesEndpoints.List(
            await Query<string>(context, "agentId"), await Query<string>(context, "kind"), await Query<string>(context, "tag"),
            await Query<bool>(context, "includeArchived"), await Query<int>(context, "page"), await Query<int>(context, "pageSize"),
            context, _memory, _config, CancellationToken.None);
    }

    private async Task<Results<Ok<MemoryDto>, ApiProblemResult>> GetAsync(string? userId, string id, string? agentId = null)
    {
        var context = Context(userId, null, ("agentId", agentId));
        return await AgentMemoriesEndpoints.Get(
            id, await Query<string>(context, "agentId"), context, _store, _config, _loggerFactory, CancellationToken.None);
    }

    private async Task<Results<NoContent, ApiProblemResult>> ForgetAsync(string? userId, string id, string? hard = null, string? role = null)
    {
        var context = Context(userId, role, ("hard", hard));
        return await AgentMemoriesEndpoints.Forget(
            id, await Query<bool>(context, "hard"), context, _memory, _store, _config, _loggerFactory, CancellationToken.None);
    }

    private DefaultHttpContext Context(string? userId, string? role, params (string Name, string? Value)[] query)
    {
        var identity = new ClaimsIdentity();
        if (userId is not null)
        {
            var claims = new List<Claim> { new("sub", userId) };
            if (role is not null)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            identity = new ClaimsIdentity(claims, "Test");
        }

        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = _services };
        context.Response.Body = new MemoryStream();
        context.Request.QueryString = QueryString.Create(
            query.Where(pair => pair.Value is not null).Select(pair => new KeyValuePair<string, string?>(pair.Name, pair.Value)));
        return context;
    }

    /// <summary>Binds the handler's own <c>ApiQuery</c> parameter called <paramref name="name"/>, as the request delegate does.</summary>
    private static async Task<ApiQuery<T>> Query<T>(HttpContext context, string name)
        where T : IParsable<T>
    {
        var parameter = typeof(AgentMemoriesEndpoints).GetMethods().SelectMany(method => method.GetParameters())
            .First(p => p.ParameterType == typeof(ApiQuery<T>) && string.Equals(p.Name, name, StringComparison.Ordinal));
        return await Bind<ApiQuery<T>>(context, parameter);
    }

    private static async Task<TSelf> Bind<TSelf>(HttpContext context, ParameterInfo parameter)
        where TSelf : class, IBindableFromHttpContext<TSelf> => (await TSelf.BindAsync(context, parameter))!;

    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
