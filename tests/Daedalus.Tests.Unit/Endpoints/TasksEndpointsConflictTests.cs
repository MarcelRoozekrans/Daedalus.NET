using System.Reflection;
using System.Text.Json;
using Daedalus.Api.Endpoints;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services;
using Microsoft.AspNetCore.Http;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>Amendment A7: a live-run refusal is 409, not 400.</summary>
public sealed class TasksEndpointsConflictTests
{
    private const string LiveError = TaskRunGuard.LiveRunPrefix + ": run x is Running. Wait until it finishes, or cancel it first.";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly IApplicationCommands _commands = Substitute.For<IApplicationCommands>();

    /// <summary>Red: drop the <c>IsConflict</c> block in <c>UpdateTask</c>; the result is 400.</summary>
    [Fact]
    public async Task A_live_run_refusal_of_an_update_is_409()
    {
        _commands.UpdateTaskAsync(default, default).ReturnsForAnyArgs(Result<TaskDto>.Failure(LiveError));

        var result = await TasksEndpoints.UpdateTask(
            Guid.NewGuid(), await BindUpdate(new UpdateTaskDto(null, null, null, null, null, null, null)), _commands,
            CancellationToken.None);

        result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    /// <summary>Red: drop the <c>IsConflict</c> block in <c>DeleteTask</c>; the result is 400.</summary>
    [Fact]
    public async Task A_live_run_refusal_of_a_delete_is_409()
    {
        _commands.DeleteTaskAsync(default, default).ReturnsForAnyArgs(Result.Failure(LiveError));

        var result = await TasksEndpoints.DeleteTask(Guid.NewGuid(), _commands, CancellationToken.None);

        result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    /// <summary>
    ///     A save that lost a race with another write is a conflict too.
    ///     Red: <c>result.Error.StartsWith(TaskRunGuard.LiveRunPrefix, StringComparison.Ordinal)</c> in place of
    ///     <c>TaskRunGuard.IsConflict(result.Error)</c> in <c>UpdateTask</c>, the old check; the result is 400.
    /// </summary>
    [Fact]
    public async Task A_lost_race_on_an_update_is_409()
    {
        _commands.UpdateTaskAsync(default, default).ReturnsForAnyArgs(Result<TaskDto>.Failure(TaskRunGuard.ChangedUnderneath(Guid.NewGuid())));

        var result = await TasksEndpoints.UpdateTask(
            Guid.NewGuid(), await BindUpdate(new UpdateTaskDto(null, null, null, null, null, null, null)), _commands,
            CancellationToken.None);

        result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    /// <summary>
    ///     Red: <c>result.Error.StartsWith(TaskRunGuard.LiveRunPrefix, StringComparison.Ordinal)</c> in place of
    ///     <c>TaskRunGuard.IsConflict(result.Error)</c> in <c>DeleteTask</c>, the old check; the result is 400.
    /// </summary>
    [Fact]
    public async Task A_lost_race_on_a_delete_is_409()
    {
        _commands.DeleteTaskAsync(default, default).ReturnsForAnyArgs(Result.Failure(TaskRunGuard.ChangedUnderneath(Guid.NewGuid())));

        var result = await TasksEndpoints.DeleteTask(Guid.NewGuid(), _commands, CancellationToken.None);

        result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    /// <summary>Binds <paramref name="dto"/> as the request delegate binds the body, through its JSON text.</summary>
    private static async Task<ApiBody<UpdateTaskDto>> BindUpdate(UpdateTaskDto dto)
    {
        await using var services = Services();
        var context = WithJsonBody(services, JsonSerializer.Serialize(dto, WebJson));
        return await BindAsync<ApiBody<UpdateTaskDto>>(context, UpdateParameter());
    }

    private static ParameterInfo UpdateParameter() =>
        typeof(TasksEndpoints).GetMethod(nameof(TasksEndpoints.UpdateTask))!.GetParameters()
            .Single(p => p.ParameterType == typeof(ApiBody<UpdateTaskDto>));
}
