using Daedalus.Agents.Scheduling;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Pins the seam <c>SchedulesController</c> is: that <see cref="IScheduleDiagnostics"/> resolves from the real
///     composed <c>Daedalus.Api</c> host as <see cref="ScheduleDiagnostics"/> (the same host-boot pattern
///     <c>ApiHostSchedulingWiringTests</c> uses), and that the overview endpoint returns exactly what the service
///     reports for a seeded schedule — the controller must add no logic of its own on top.
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

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string user)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderTestAuthHandler.UserHeader, user);
        return await _client.SendAsync(request);
    }
}
