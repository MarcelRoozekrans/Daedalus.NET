using System.Security.Claims;
using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Endpoints;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Tests.Shared;
using Daedalus.Tests.Unit.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     Covers <see cref="WorkflowRunsEndpoints.Start"/>'s mapping of a <see cref="ManufactureStartFailureKind"/> to an
///     HTTP status and header. A host with the engine off answers every route with the engine filter's 503 before the
///     handler runs, so the disabled mapping is reachable only here and not over HTTP.
/// </summary>
public sealed class WorkflowRunsEndpointsStartTests : IAsyncLifetime
{
    private readonly IManufactureRunStarter _starter = Substitute.For<IManufactureRunStarter>();
    private readonly WorkflowRunsRequest _request;

    public WorkflowRunsEndpointsStartTests()
    {
        var gateway = new WorkflowRunGateway(
            Substitute.For<IWorkflowStore>(), Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), Substitute.For<IRunWorkspaceProvider>(), new WorkflowConfig(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);

        var identity = new ClaimsIdentity([new Claim("sub", "u-dev"), new Claim(ClaimTypes.Role, "developer")], authenticationType: "test");
        _request = WorkflowRunsRequest.For(gateway, new ClaimsPrincipal(identity));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _request.DisposeAsync();

    /// <summary>
    ///     Each kind has its own status, and a refusal's message is the problem detail. Red, per row: map that kind to
    ///     another status in <c>ManufactureStartProblem.StatusOf</c>'s switch, and its row fails.
    /// </summary>
    [Theory]
    [InlineData(ManufactureStartFailureKind.Invalid, StatusCodes.Status400BadRequest)]
    [InlineData(ManufactureStartFailureKind.Unstartable, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(ManufactureStartFailureKind.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(ManufactureStartFailureKind.Disabled, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(ManufactureStartFailureKind.Failed, StatusCodes.Status500InternalServerError)]
    public async Task A_failure_kind_maps_to_its_status_with_the_message_as_the_detail(ManufactureStartFailureKind kind, int status)
    {
        var result = await StartWithAsync(kind);

        var problem = result.Result.Should().BeOfType<ApiProblemResult>().Subject;
        // Red, per row: StatusCodes.Status418ImATeapot in place of that kind's status in ManufactureStartProblem.StatusOf.
        problem.StatusCode.Should().Be(status);
        // Red: ApiProblem.Of(context, StatusOf(failure, context.Response)) without failure.Message in
        //      ManufactureStartProblem.From.
        problem.ProblemDetails.Detail.Should().Be("the message");
    }

    /// <summary>A transient outage tells the client to retry in 30 seconds.</summary>
    [Fact]
    public async Task An_unavailable_start_carries_retry_after_30()
    {
        await StartWithAsync(ManufactureStartFailureKind.Unavailable);

        // Red: response.Headers.Remove("Retry-After") in place of setting it in the Unavailable case of
        //      ManufactureStartProblem.StatusOf.
        _request.Context.Response.Headers.RetryAfter.ToString().Should().Be("30");
    }

    /// <summary>A disabled engine is a host setting that no retry gets past, so no <c>Retry-After</c>.</summary>
    [Fact]
    public async Task A_disabled_engine_carries_no_retry_after()
    {
        await StartWithAsync(ManufactureStartFailureKind.Disabled);

        // Red: the Disabled case of ManufactureStartProblem.StatusOf sharing the Unavailable branch; the header appears.
        _request.Context.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
    }

    /// <summary>The other kinds send no <c>Retry-After</c> either.</summary>
    [Fact]
    public async Task An_invalid_start_carries_no_retry_after()
    {
        await StartWithAsync(ManufactureStartFailureKind.Invalid);

        // Red: Retry-After set ahead of the switch in ManufactureStartProblem.StatusOf, on every failure.
        _request.Context.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
    }

    private async Task<Results<Created<StartWorkflowRunResponse>, ApiProblemResult>> StartWithAsync(ManufactureStartFailureKind kind)
    {
        _starter.StartAsync(Arg.Any<ManufactureStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<Guid, ManufactureStartFailure>>(
                Result<Guid, ManufactureStartFailure>.Failure(new ManufactureStartFailure(kind, "the message"))));

        var body = await _request.BodyAsync(nameof(WorkflowRunsEndpoints.Start), new StartWorkflowRunRequest("Tighten a guard.", "sandbox"));
        return await WorkflowRunsEndpoints.Start(body, _request.Context, _starter, CancellationToken.None);
    }
}
