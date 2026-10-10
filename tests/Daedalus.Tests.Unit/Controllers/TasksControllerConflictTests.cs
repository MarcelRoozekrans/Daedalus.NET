using Daedalus.Api.Controllers;
using Daedalus.Api.Services;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Unit.Controllers;

/// <summary>Amendment A7: a live-run refusal is 409, not 400.</summary>
public sealed class TasksControllerConflictTests
{
    private const string LiveError = TaskRunGuard.LiveRunPrefix + ": run x is Running. Wait until it finishes, or cancel it first.";

    private readonly IApplicationCommands _commands = Substitute.For<IApplicationCommands>();

    private TasksController Controller() =>
        new(Substitute.For<ITaskQueryService>(), _commands, NullLogger<TasksController>.Instance);

    /// <summary>Red: drop the prefix check in <c>UpdateTask</c>; the result is 400.</summary>
    [Fact]
    public async Task A_live_run_refusal_of_an_update_is_409()
    {
        _commands.UpdateTaskAsync(default, default).ReturnsForAnyArgs(Result<TaskDto>.Failure(LiveError));

        var result = await Controller().UpdateTask(Guid.NewGuid(), new UpdateTaskDto(null, null, null, null, null, null, null));

        result.Should().BeOfType<ConflictObjectResult>();
    }

    /// <summary>Red: drop the prefix check in <c>DeleteTask</c>; the result is 400.</summary>
    [Fact]
    public async Task A_live_run_refusal_of_a_delete_is_409()
    {
        _commands.DeleteTaskAsync(default, default).ReturnsForAnyArgs(Result.Failure(LiveError));

        var result = await Controller().DeleteTask(Guid.NewGuid());

        result.Should().BeOfType<ConflictObjectResult>();
    }

    /// <summary>
    ///     A save that lost a race with another write is a conflict too.
    ///     Red: revert <c>UpdateTask</c> to the old <c>StartsWith(LiveRunPrefix)</c> check; the result is 400.
    /// </summary>
    [Fact]
    public async Task A_lost_race_on_an_update_is_409()
    {
        _commands.UpdateTaskAsync(default, default).ReturnsForAnyArgs(Result<TaskDto>.Failure(TaskRunGuard.ChangedUnderneath(Guid.NewGuid())));

        var result = await Controller().UpdateTask(Guid.NewGuid(), new UpdateTaskDto(null, null, null, null, null, null, null));

        result.Should().BeOfType<ConflictObjectResult>();
    }

    /// <summary>Red: revert <c>DeleteTask</c> to the old <c>StartsWith(LiveRunPrefix)</c> check; the result is 400.</summary>
    [Fact]
    public async Task A_lost_race_on_a_delete_is_409()
    {
        _commands.DeleteTaskAsync(default, default).ReturnsForAnyArgs(Result.Failure(TaskRunGuard.ChangedUnderneath(Guid.NewGuid())));

        var result = await Controller().DeleteTask(Guid.NewGuid());

        result.Should().BeOfType<ConflictObjectResult>();
    }
}
