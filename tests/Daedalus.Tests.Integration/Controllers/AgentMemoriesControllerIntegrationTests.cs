using System.Security.Claims;
using Daedalus.Agents;
using Daedalus.Agents.Memory;
using Daedalus.Api.Controllers;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos;
using Thalos.Memory;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     <see cref="AgentMemoriesController"/> over the real <see cref="PostgresMemoryStore"/> (fixture database) and a fake
///     <see cref="IMemoryService"/>: which owners a caller may read, that foreign ids answer 404 rather than 403, and that
///     forgetting a shared-owner memory needs the developer policy.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AgentMemoriesControllerIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Shared = "daedalus";
    private static readonly DateTimeOffset T0 = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    // ControllerBase.Problem() resolves ProblemDetailsFactory from the request services.
    private static readonly ServiceProvider RequestServices = new ServiceCollection()
        .AddLogging()
        .AddMvcCore().AddApiExplorer().Services
        .BuildServiceProvider();

    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();
    private readonly MemoryConfig _config = new() { SharedOwnerId = Shared };
    private readonly FakeTimeProvider _clock = new(T0);
    private PostgresMemoryStore _store = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _store = new PostgresMemoryStore(new FixtureDbContextFactory(fixture), _clock);
    }

    public Task DisposeAsync() => Task.CompletedTask;

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

        var result = await Controller("alice").List(kind: "Learning", pageSize: 500, page: 2, ct: CancellationToken.None);

        seen.Should().NotBeNull();
        seen!.OwnerIds.Should().Equal("alice", Shared);
        seen.Kinds.Should().ContainSingle().Which.Value.Should().Be("learning", "kinds are lower-case identifiers");
        seen.PageSize.Should().Be(MemoryQuery.MaxPageSize, "the endpoint clamps to the Thalos page bound");
        seen.Page.Should().Be(2);
        seen.IncludeArchived.Should().BeFalse();
        seen.Tags.Should().BeNull();

        var page = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PagedResultDto<MemoryDto>>().Subject;
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

        var result = await Controller(Shared).List(tag: " Database ", includeArchived: true, ct: CancellationToken.None);

        seen!.OwnerIds.Should().Equal(Shared);
        seen.Tags.Should().Equal("Database");
        seen.IncludeArchived.Should().BeTrue();
        var page = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PagedResultDto<MemoryDto>>().Subject;
        page.Items.Should().ContainSingle().Which.IsShared.Should().BeTrue();
    }

    [Fact]
    public async Task List_maps_a_service_failure_to_its_status()
    {
        _memory.ListAsync(Arg.Any<MemoryQuery>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<MemoryPage, AgentError>.Failure(AgentError.MemoryStoreFailed("the store is unhappy")));

        var result = await Controller("alice").List(ct: CancellationToken.None);

        AssertProblem(result, StatusCodes.Status502BadGateway, "MemoryStoreFailed");
    }

    [Theory]
    [InlineData("not-an-agent", null)]
    [InlineData(null, "Not a Kind!")]
    public async Task List_rejects_an_unparsable_filter_and_never_calls_the_service(string? agentId, string? kind)
    {
        var result = await Controller("alice").List(agentId: agentId, kind: kind, ct: CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest, "Validation");
        await _memory.DidNotReceiveWithAnyArgs().ListAsync(null!, default);
    }

    [Fact]
    public async Task Get_own_and_shared_are_visible_foreign_is_404()
    {
        var mine = await SeedAsync("alice", "mine");
        var shared = await SeedAsync(Shared, "the project's");
        var foreign = await SeedAsync("bob", "bob's");

        var own = await Controller("alice").Get(mine.Id.ToString(), CancellationToken.None);
        own.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MemoryDto>()
            .Subject.Should().BeEquivalentTo(new { Id = mine.Id.ToString(), Text = "mine", IsShared = false });

        var project = await Controller("alice").Get(shared.Id.ToString(), CancellationToken.None);
        project.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MemoryDto>()
            .Subject.Should().BeEquivalentTo(new { Id = shared.Id.ToString(), Text = "the project's", IsShared = true });

        AssertProblem(await Controller("alice").Get(foreign.Id.ToString(), CancellationToken.None), StatusCodes.Status404NotFound, "MemoryNotFound");
        AssertProblem(await Controller("alice").Get(MemoryId.New().ToString(), CancellationToken.None), StatusCodes.Status404NotFound, "MemoryNotFound");
    }

    [Fact]
    public async Task Forget_own_archives_under_the_callers_scope()
    {
        var mine = await SeedAsync("alice", "mine");
        _memory.ForgetAsync(Arg.Any<MemoryId>(), Arg.Any<MemoryScope>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.UnitResult<AgentError>.Success());

        var result = await Controller("alice").Forget(mine.Id.ToString(), hard: false, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
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

        var result = await Controller("alice").Forget(mine.Id.ToString(), hard: true, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        await _memory.Received(1).ForgetAsync(mine.Id, Arg.Any<MemoryScope>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Forget_shared_requires_developer_or_admin()
    {
        var shared = await SeedAsync(Shared, "the project's");
        _memory.ForgetAsync(Arg.Any<MemoryId>(), Arg.Any<MemoryScope>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.UnitResult<AgentError>.Success());

        var denied = await Controller("alice").Forget(shared.Id.ToString(), hard: false, CancellationToken.None);
        AssertProblem(denied, StatusCodes.Status403Forbidden, "MemoryForbidden");
        await _memory.DidNotReceiveWithAnyArgs().ForgetAsync(default, default, default, default);

        var developer = await Controller("carol", "developer").Forget(shared.Id.ToString(), hard: false, CancellationToken.None);
        developer.Should().BeOfType<NoContentResult>();
        var admin = await Controller("dave", "admin").Forget(shared.Id.ToString(), hard: false, CancellationToken.None);
        admin.Should().BeOfType<NoContentResult>();

        await _memory.Received(2).ForgetAsync(shared.Id, Arg.Is<MemoryScope>(s => s.OwnerId == Shared), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Forget_foreign_answers_404_and_never_calls_the_service()
    {
        var foreign = await SeedAsync("bob", "bob's");

        var result = await Controller("alice").Forget(foreign.Id.ToString(), hard: false, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status404NotFound, "MemoryNotFound");
        await _memory.DidNotReceiveWithAnyArgs().ForgetAsync(default, default, default, default);
    }

    [Fact]
    public async Task Invalid_id_is_400_on_get_and_forget()
    {
        AssertProblem(await Controller("alice").Get("not-an-id", CancellationToken.None), StatusCodes.Status400BadRequest, "Validation");
        AssertProblem(await Controller("alice").Forget("not-an-id", hard: false, CancellationToken.None), StatusCodes.Status400BadRequest, "Validation");
        await _memory.DidNotReceiveWithAnyArgs().ForgetAsync(default, default, default, default);
    }

    [Fact]
    public async Task Anonymous_is_401_on_every_endpoint()
    {
        (await Anonymous().List(ct: CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        (await Anonymous().Get(MemoryId.New().ToString(), CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        (await Anonymous().Forget(MemoryId.New().ToString(), hard: false, CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();

        await _memory.DidNotReceiveWithAnyArgs().ListAsync(null!, default);
        await _memory.DidNotReceiveWithAnyArgs().ForgetAsync(default, default, default, default);
    }

    // ---------- helpers ----------

    private async Task<MemoryRecord> SeedAsync(string ownerId, string text)
    {
        var record = new MemoryRecord
        {
            Id = MemoryId.New(),
            OwnerId = ownerId,
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

    private static void AssertProblem(IActionResult result, int status, string code)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(status);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(status);
        problem.Title.Should().Be(code);
        problem.Extensions.Should().ContainKey("code").WhoseValue.Should().Be(code);
    }

    private AgentMemoriesController Controller(string userId, string? role = null)
    {
        var claims = new List<Claim> { new("sub", userId) };
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return Build(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")));
    }

    private AgentMemoriesController Anonymous() => Build(new ClaimsPrincipal(new ClaimsIdentity()));

    private AgentMemoriesController Build(ClaimsPrincipal user)
    {
        var httpContext = new DefaultHttpContext { User = user, RequestServices = RequestServices };
        httpContext.Response.Body = new MemoryStream();

        return new AgentMemoriesController(_memory, _store, _config, NullLogger<AgentMemoriesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
