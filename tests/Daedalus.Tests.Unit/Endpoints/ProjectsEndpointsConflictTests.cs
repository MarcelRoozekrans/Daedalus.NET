using Daedalus.Api.Endpoints;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Microsoft.AspNetCore.Http;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>Amendment A7: a project delete refused for a live task run is 409, not 400.</summary>
public sealed class ProjectsEndpointsConflictTests
{
    private readonly IApplicationCommands _commands = Substitute.For<IApplicationCommands>();

    /// <summary>Red: drop the <c>IsConflict</c> check in <c>DeleteProject</c>; the result is 400.</summary>
    [Fact]
    public async Task A_live_run_refusal_of_a_project_delete_is_409()
    {
        _commands.DeleteProjectAsync(default, default).ReturnsForAnyArgs(
            Result.Failure(TaskRunGuard.LiveRunPrefix + ": task T-1's run x is Running."));

        var result = await ProjectsEndpoints.DeleteProject(Guid.NewGuid(), _commands, CancellationToken.None);

        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    /// <summary>Red: map every failure to 409; a missing project stops answering 404.</summary>
    [Fact]
    public async Task A_missing_project_is_still_404()
    {
        _commands.DeleteProjectAsync(default, default).ReturnsForAnyArgs(Result.Failure("Project x not found"));

        var result = await ProjectsEndpoints.DeleteProject(Guid.NewGuid(), _commands, CancellationToken.None);

        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }
}
