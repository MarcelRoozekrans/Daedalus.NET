using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Api;

/// <summary>
///     Pins the 400 response shape for <c>POST /api/tasks</c> across phase 1.7's ZeroAlloc.Validation
///     migration. <c>Daedalus.Api.Middleware.ZeroAllocValidationFilter</c> wraps
///     <c>Daedalus.Application.DTOs.CreateTaskDtoValidator</c>'s failures into an RFC 7807
///     <see cref="ValidationProblemDetails"/> body; this test is the guard that the swap had to reproduce exactly —
///     status, <c>Title</c>, <c>Type</c>, and the per-property error messages keyed by property name — not merely
///     "still 400".
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ValidationContractTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly IAgentRuntime _runtime = Substitute.For<IAgentRuntime>();
    private ApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _factory = new ApiWebApplicationFactory(fixture.ConnectionString, _runtime);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Posting_an_invalid_task_returns_400_with_rfc7807_validation_problem_details()
    {
        var response = await Send(new
        {
            projectId = Guid.NewGuid(),
            title = "",                 // violates NotEmpty
            description = "d",
            prompt = "p",
            completionPromise = "c",
            maxIterations = 5000,       // violates InclusiveBetween 1..1000
            parallelGroup = 1,
            priority = 0,
            estimatedComplexity = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(400);
        problem.Title.Should().Be("One or more validation errors occurred.");
        problem.Type.Should().Be("https://tools.ietf.org/html/rfc7231#section-6.5.1");

        problem.Errors.Should().ContainKey("Title");
        problem.Errors["Title"].Should().Contain("Title is required.");
        problem.Errors.Should().ContainKey("MaxIterations");
        problem.Errors["MaxIterations"].Should().Contain("Max iterations must be between 1 and 1000.");
    }

    /// <summary>
    ///     Guards two of <c>UpdateTaskDto</c>'s four <c>When = nameof(...)HasValue</c> guards —
    ///     <see cref="Daedalus.Application.DTOs.UpdateTaskDto.MaxIterations"/> (<c>InclusiveBetween(1, 1000)</c>)
    ///     and <see cref="Daedalus.Application.DTOs.UpdateTaskDto.ParallelGroup"/>
    ///     (<c>GreaterThanOrEqualTo(1)</c>) — because dropping either guard is behaviourally observable: an
    ///     omitted (<c>null</c>) field coerces to <c>0</c> before its range check runs, and <c>0</c> is outside
    ///     both ranges, so the partial <c>PUT</c> would wrongly 400.
    ///     It CANNOT guard <c>Priority</c> or <c>EstimatedComplexity</c> — both are <c>InclusiveBetween(0, n)</c>,
    ///     so a coerced <c>0</c> is a valid value (0 = Critical priority, 0 = Simple complexity) and produces the
    ///     same 200 whether the guard is present or not; no status-code assertion can tell the two cases apart.
    ///     <c>Daedalus.Tests.Unit.Application.Validators.UpdateTaskDtoWhenGuardTests</c> closes that gap
    ///     structurally, by reflecting on the attributes instead of asserting on behaviour.
    /// </summary>
    [Fact]
    public async Task Partial_update_touching_only_title_succeeds()
    {
        var projectId = Guid.NewGuid();
        await using (var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(fixture.ConnectionString)))
        {
            db.Projects.Add(IntegrationTestFactory.CreateProject(projectId));
            await db.SaveChangesAsync();
        }

        var create = await Send(new
        {
            projectId,
            title = "Original title",
            description = "d",
            prompt = "p",
            completionPromise = "c",
            maxIterations = 5,
            parallelGroup = 1,
            priority = 0,
            estimatedComplexity = 0
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var created = await create.Content.ReadFromJsonAsync<TaskDto>();

        var response = await SendPut(created!.Id, new { title = "Updated title" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var updated = await response.Content.ReadFromJsonAsync<TaskDto>();
        updated!.Title.Should().Be("Updated title");
        updated.MaxIterations.Should().Be(5, "an omitted MaxIterations must not be coerced to 0 and re-validated");
        updated.ParallelGroup.Should().Be(1, "an omitted ParallelGroup must not be coerced to 0 and re-validated");
    }

    /// <summary>
    ///     <c>CreateTask</c> sits behind both the class-level authenticated-user check and the
    ///     <c>TaskManagement</c> policy (role <c>task-manager</c> or <c>admin</c>) — without the roles header this
    ///     would 403 before the ZeroAlloc.Validation action filter ever runs, masking the contract under test.
    /// </summary>
    private async Task<HttpResponseMessage> Send(object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/tasks", UriKind.Relative));
        request.Headers.Add(HeaderTestAuthHandler.UserHeader, "alice");
        request.Headers.Add(HeaderTestAuthHandler.RolesHeader, "task-manager");
        request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendPut(Guid id, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/tasks/{id}", UriKind.Relative));
        request.Headers.Add(HeaderTestAuthHandler.UserHeader, "alice");
        request.Headers.Add(HeaderTestAuthHandler.RolesHeader, "task-manager");
        request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }
}
