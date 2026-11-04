using System.Reflection;
using Daedalus.Api.Endpoints;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs.Scheduling;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using ZeroAlloc.Results;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="SchedulesEndpoints"/> answers what <c>SchedulesController</c> answered: the two reads call straight
///     through to <see cref="IScheduleDiagnostics"/> and return its list as a 200, and the resend maps the service's
///     result to an empty 200 or a 400 carrying <c>{ "error": ... }</c>.
/// </summary>
public sealed class SchedulesEndpointsTests
{
    private static readonly Guid ScheduleId = Guid.NewGuid();
    private static readonly Guid ExecutionId = Guid.NewGuid();

    [Fact]
    public async Task GetOverview_returns_what_the_service_reports()
    {
        var diagnosis = Diagnosis();
        var diagnostics = Substitute.For<IScheduleDiagnostics>();
        diagnostics.GetOverviewAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<RunDiagnosis>>([diagnosis]));

        Ok<IReadOnlyList<RunDiagnosis>> result = await SchedulesEndpoints.GetOverview(diagnostics, CancellationToken.None);

        // Red: return TypedResults.Ok<IReadOnlyList<RunDiagnosis>>([]) from GetOverview; the diagnosis is lost.
        result.Value.Should().Equal(diagnosis);
        // Red: call diagnostics.GetRunHistoryAsync instead of GetOverviewAsync in GetOverview.
        await diagnostics.Received(1).GetOverviewAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRunHistory_passes_the_schedule_and_the_sent_take()
    {
        var diagnosis = Diagnosis();
        var diagnostics = Substitute.For<IScheduleDiagnostics>();
        diagnostics.GetRunHistoryAsync(ScheduleId, 7, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<RunDiagnosis>>([diagnosis]));
        await using var services = Services();
        var context = Create(services);
        context.Request.QueryString = new QueryString("?take=7");
        var take = await BindAsync<ApiQuery<int>>(context, Parameter(nameof(TakeQuery)));

        Ok<IReadOnlyList<RunDiagnosis>> result =
            await SchedulesEndpoints.GetRunHistory(ScheduleId, take, diagnostics, CancellationToken.None);

        // Red: pass 20 in place of take.ValueOr(DefaultTake) in the call to the service; the stub for 7 is not met.
        // Red: pass Guid.Empty in place of scheduleId in the call to the service; the stub for the schedule is not met.
        result.Value.Should().Equal(diagnosis);
    }

    [Fact]
    public async Task GetRunHistory_without_take_asks_for_twenty()
    {
        var diagnostics = Substitute.For<IScheduleDiagnostics>();
        diagnostics.GetRunHistoryAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<RunDiagnosis>>([]));
        await using var services = Services();
        var take = await BindAsync<ApiQuery<int>>(Create(services), Parameter(nameof(TakeQuery)));

        await SchedulesEndpoints.GetRunHistory(ScheduleId, take, diagnostics, CancellationToken.None);

        // Red: pass take.Value instead of take.ValueOr(20) in GetRunHistory; the service is asked for 0.
        await diagnostics.Received(1).GetRunHistoryAsync(ScheduleId, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resend_answers_an_empty_ok_when_the_service_requeues()
    {
        var actions = Substitute.For<IScheduleDeliveryActions>();
        actions.RequeueAsync(ExecutionId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Success()));

        var result = await SchedulesEndpoints.Resend(ScheduleId, ExecutionId, actions, CancellationToken.None);

        // Red: return TypedResults.BadRequest(new ErrorBody("")) for a success in Resend.
        result.Should().BeOfType<Ok>();
        // Red: pass Guid.Empty instead of executionId to RequeueAsync.
        await actions.Received(1).RequeueAsync(ExecutionId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resend_answers_400_with_the_services_reason_when_it_fails()
    {
        var actions = Substitute.For<IScheduleDeliveryActions>();
        actions.RequeueAsync(ExecutionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure("nothing to resend")));

        var result = await SchedulesEndpoints.Resend(ScheduleId, ExecutionId, actions, CancellationToken.None);

        // Red: answer TypedResults.StatusCode(400), a bodiless 400, for a failure in Resend.
        var badRequest = result.Should().BeOfType<BadRequest<ErrorBody>>().Subject;
        // Red: write the literal "Internal server error" or an empty string in place of result.Error.
        badRequest.Value.Should().Be(new ErrorBody("nothing to resend"));
    }

    private static RunDiagnosis Diagnosis() =>
        new(RunVerdict.Failed, ScheduleId, "digest", new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc), 2, 2, "boom", 1,
            null, null, new DateTime(2026, 11, 1, 6, 0, 1, DateTimeKind.Utc), new DateTime(2026, 11, 1, 6, 0, 2, DateTimeKind.Utc),
            ExecutionId, true, null, null, null);

    private static ParameterInfo Parameter(string probe) =>
        typeof(SchedulesEndpointsTests).GetMethod(probe, BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters()[0];

    // The handler parameter ApiQuery reads its name from.
    private static void TakeQuery(ApiQuery<int> take) => _ = take;
}
