using System.Security.Claims;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Tests.Unit.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Controllers;

/// <summary>
///     Covers <see cref="WorkflowRunsController.Start"/>'s mapping of a <see cref="ManufactureStartFailureKind"/> to an
///     HTTP status and header. A host with the engine off cannot construct this controller, since
///     <see cref="WorkflowRunGateway"/> is registered only when the engine is on, so the disabled mapping is reachable
///     only here and not over HTTP.
/// </summary>
public sealed class WorkflowRunsControllerStartTests
{
    private readonly IManufactureRunStarter _starter = Substitute.For<IManufactureRunStarter>();
    private readonly WorkflowRunsController _controller;

    public WorkflowRunsControllerStartTests()
    {
        var gateway = new WorkflowRunGateway(
            Substitute.For<IWorkflowStore>(), Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddApiExplorer();

        var identity = new ClaimsIdentity([new Claim("sub", "u-dev"), new Claim(ClaimTypes.Role, "developer")], authenticationType: "test");
        _controller = new WorkflowRunsController(gateway)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = new ClaimsPrincipal(identity) },
            },
        };
    }

    /// <summary>
    ///     Each kind has its own status, and a refusal's message is the problem detail. Red, per row: map that kind to
    ///     another status in the controller's switch, and its row fails.
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

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(status);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be("the message");
    }

    /// <summary>
    ///     A transient outage tells the client to retry in 30 seconds. Red: leave the header out of the
    ///     <c>Unavailable</c> case, and it is absent.
    /// </summary>
    [Fact]
    public async Task An_unavailable_start_carries_retry_after_30()
    {
        await StartWithAsync(ManufactureStartFailureKind.Unavailable);

        _controller.Response.Headers.RetryAfter.ToString().Should().Be("30");
    }

    /// <summary>
    ///     A disabled engine is a host setting that no retry gets past, so no <c>Retry-After</c>. Red: let the
    ///     <c>Disabled</c> case share the <c>Unavailable</c> branch, and the header appears.
    /// </summary>
    [Fact]
    public async Task A_disabled_engine_carries_no_retry_after()
    {
        await StartWithAsync(ManufactureStartFailureKind.Disabled);

        _controller.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
    }

    /// <summary>Red, for the other kinds: send a <c>Retry-After</c> on every failure, and the 400 carries one.</summary>
    [Fact]
    public async Task An_invalid_start_carries_no_retry_after()
    {
        await StartWithAsync(ManufactureStartFailureKind.Invalid);

        _controller.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
    }

    private async Task<IActionResult> StartWithAsync(ManufactureStartFailureKind kind)
    {
        _starter.StartAsync(Arg.Any<ManufactureStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<Guid, ManufactureStartFailure>>(
                Result<Guid, ManufactureStartFailure>.Failure(new ManufactureStartFailure(kind, "the message"))));

        return await _controller.Start(new StartWorkflowRunRequest("Tighten a guard.", "sandbox"), _starter, CancellationToken.None);
    }
}
