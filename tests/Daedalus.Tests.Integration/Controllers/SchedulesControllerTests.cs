using Daedalus.Agents.Channels;
using Daedalus.Agents.Scheduling;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Pins the seam <c>SchedulesController</c> is: that <see cref="IScheduleDiagnostics"/> and
///     <see cref="IScheduleDeliveryActions"/> resolve from the real composed <c>Daedalus.Api</c> host as their
///     real implementations (the same host-boot pattern <c>ApiHostSchedulingWiringTests</c> uses), that the
///     overview endpoint returns exactly what the service reports for a seeded schedule, and that the resend
///     route both requires authentication and surfaces the contract's failed state — rather than silently
///     succeeding — when there is nothing to resend. The controller must add no logic of its own on top.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SchedulesControllerTests(PostgresFixture fixture) : IAsyncLifetime
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
    public void The_api_host_resolves_schedule_diagnostics_as_the_real_implementation()
    {
        // Like every scheduling dispatcher in ApiHostSchedulingWiringTests, ScheduleDiagnostics depends on the
        // scoped ApplicationDbContext, so it cannot resolve from the host's root provider — a scope is required.
        using var scope = _factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IScheduleDiagnostics>().Should().BeOfType<ScheduleDiagnostics>();
    }

    [Fact]
    public void The_api_host_resolves_schedule_delivery_actions_as_the_real_implementation()
    {
        using var scope = _factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IScheduleDeliveryActions>().Should().BeOfType<ScheduleDeliveryActions>();
    }

    [Fact]
    public async Task Overview_endpoint_requires_authentication()
    {
        var response = await _client.GetAsync(new Uri("/api/schedules", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Overview_endpoint_returns_the_seeded_schedule_with_its_verdict()
    {
        // Disabled, not merely due in the future: Disabled outranks every other verdict regardless of NextRunAt,
        // so the expected answer does not depend on the real TimeProvider the production host wires.
        var schedule = ScheduledRun.Create(
            "morning-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337",
            "schedule:daedalus", ["reader", "writer"], ScheduleOrigin.Config, DateTime.UtcNow.AddDays(-1)).Value;
        schedule.Disable();

        await using (var db = fixture.CreateDbContext())
        {
            db.ScheduledRuns.Add(schedule);
            await db.SaveChangesAsync();
        }

        var response = await Send(HttpMethod.Get, "/api/schedules", user: "alice");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var overview = await response.Content.ReadFromJsonAsync<List<RunDiagnosis>>();

        // Not ContainSingle: the host also reconciles a "daily-digest" schedule from configuration (see
        // ScheduleReconciler), so the seeded schedule is picked out by id rather than assuming it is alone.
        var diagnosis = overview.Should().ContainSingle(d => d.ScheduleId == schedule.Id).Subject;
        diagnosis.ScheduleId.Should().Be(schedule.Id);
        diagnosis.ScheduleName.Should().Be("morning-digest");
        diagnosis.Verdict.Should().Be(RunVerdict.Disabled);
        diagnosis.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Resend_endpoint_requires_authentication()
    {
        var response = await _client.PostAsync(
            new Uri($"/api/schedules/{Guid.NewGuid()}/runs/{Guid.NewGuid()}/resend", UriKind.Relative), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Resend_endpoint_surfaces_the_failed_state_when_there_is_nothing_to_resend()
    {
        // The route and the DI wiring both work — a random execution id that never queued anything is the
        // ordinary "already resolved" case the contract's failed resend state exists for, not a wiring bug.
        var scheduleId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        var response = await Send(HttpMethod.Post, $"/api/schedules/{scheduleId}/runs/{executionId}/resend", user: "alice");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a resend that finds nothing to requeue must surface as a failure the page can render, not a 200 that silently did nothing");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(executionId.ToString());
    }

    [Fact]
    public async Task Resend_endpoint_requeues_a_real_dead_lettered_message_end_to_end()
    {
        var schedule = ScheduledRun.Create(
            "morning-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337",
            "schedule:daedalus", ["reader", "writer"], ScheduleOrigin.Config, DateTime.UtcNow.AddHours(1)).Value;

        var occurrenceAtUtc = DateTime.UtcNow.AddHours(-2);
        var execution = ScheduledRunExecution.Create(
            schedule.Id, occurrenceAtUtc, schedule.ChannelId, schedule.ConversationId,
            schedule.PrincipalId, schedule.Roles, occurrenceAtUtc).Value;
        execution.BeginScout(occurrenceAtUtc);
        execution.RecordFindings("three open PRs, one failing CI run", occurrenceAtUtc.AddMinutes(1));
        execution.RecordDigest("Three PRs are waiting on you.", occurrenceAtUtc.AddMinutes(2));
        execution.Complete(occurrenceAtUtc.AddMinutes(3));

        await using (var db = fixture.CreateDbContext())
        {
            db.ScheduledRuns.Add(schedule);
            db.ScheduledRunExecutions.Add(execution);
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter<ChannelMessageQueued>>();
            await writer.WriteAsync(
                new ChannelMessageQueued("telegram", "482910337", "Three PRs are waiting on you.", execution.Id),
                ct: CancellationToken.None);
        }

        await MarkDeadLetteredAsync(execution.Id, "Telegram returned 429 after 8 attempts", retryCount: 7);

        var response = await Send(HttpMethod.Post, $"/api/schedules/{schedule.Id}/runs/{execution.Id}/resend", user: "alice");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var row = await SingleOutboxRowForAsync(execution.Id);
        row.Status.Should().Be(OutboxMessageStatus.Pending,
            "a successful resend puts the message back in the ordinary poller's path, not merely returns 200");
    }

    private async Task MarkDeadLetteredAsync(Guid executionId, string error, int retryCount)
    {
        using var scope = _factory.Services.CreateScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IOutboxSerializer>();
        var typeName = typeof(ChannelMessageQueued).FullName;

        await using var db = fixture.CreateDbContext();
        var rows = await db.OutboxMessages.Where(m => m.TypeName == typeName).ToListAsync();
        var row = rows.Single(r => serializer.Deserialize<ChannelMessageQueued>(r.Payload).ExecutionId == executionId);
        row.Status = OutboxMessageStatus.DeadLetter;
        row.DeadLetterError = error;
        row.RetryCount = retryCount;
        await db.SaveChangesAsync();
    }

    private async Task<OutboxMessageEntity> SingleOutboxRowForAsync(Guid executionId)
    {
        using var scope = _factory.Services.CreateScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IOutboxSerializer>();
        var typeName = typeof(ChannelMessageQueued).FullName;

        await using var db = fixture.CreateDbContext();
        var rows = await db.OutboxMessages.Where(m => m.TypeName == typeName).ToListAsync();
        return rows.Single(r => serializer.Deserialize<ChannelMessageQueued>(r.Payload).ExecutionId == executionId);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string user)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderTestAuthHandler.UserHeader, user);
        return await _client.SendAsync(request);
    }
}
