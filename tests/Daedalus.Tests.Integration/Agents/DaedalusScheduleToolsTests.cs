using System.Net;
using System.Net.Http.Json;
using Daedalus.Agents;
using Daedalus.Agents.Tools;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Thalos.Tools;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     <see cref="DaedalusScheduleTools"/> against the real, DI-resolved <see cref="IScheduleDiagnostics"/> —
///     the same composed <c>Daedalus.Api</c> host <see cref="Controllers.SchedulesControllerTests"/> boots — so
///     these tests exercise the actual production wiring rather than a hand-assembled substitute.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class DaedalusScheduleToolsTests(PostgresFixture fixture) : IAsyncLifetime
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
    public async Task LocalToolSource_exposes_only_the_two_read_only_tools()
    {
        using var scope = _factory.Services.CreateScope();
        var source = new LocalToolSource(
            DaedalusAgentsServiceCollectionExtensions.KnowledgeToolSourceName,
            scope.ServiceProvider,
            [typeof(DaedalusScheduleTools)]);

        var tools = await source.GetToolsAsync(CancellationToken.None);

        tools.IsSuccess.Should().BeTrue();
        tools.Value.Select(t => t.Name).Should().BeEquivalentTo(["list_schedules", "why_did_a_run_fail"],
            "no create, cancel, or enable/disable tool must ever appear here — an agent that cannot schedule " +
            "work cannot schedule work for itself");
    }

    [Fact]
    public async Task The_tool_and_the_controller_report_the_same_verdict_for_the_same_state()
    {
        // One seeded state: a schedule whose only run failed at the Writer step. Enabled and comfortably not
        // due, so neither Disabled nor Overdue can displace the alarm and muddy the comparison.
        var schedule = ScheduledRun.Create(
            "morning-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337",
            "schedule:daedalus", ["reader", "writer"], ScheduleOrigin.Config, DateTime.UtcNow.AddDays(1)).Value;

        var occurrence = DateTime.UtcNow.AddHours(-2);
        var execution = ScheduledRunExecution.Create(
            schedule.Id, occurrence, schedule.ChannelId, schedule.ConversationId,
            schedule.PrincipalId, schedule.Roles, occurrence).Value;
        execution.BeginScout(occurrence);
        execution.RecordFindings("three open PRs", occurrence.AddMinutes(1));
        execution.Fail("the writer subagent exceeded its token budget", occurrence.AddMinutes(2));

        await using (var db = fixture.CreateDbContext())
        {
            db.ScheduledRuns.Add(schedule);
            db.ScheduledRunExecutions.Add(execution);
            await db.SaveChangesAsync();
        }

        // Path 1: the controller, over HTTP, through the real composed host.
        var response = await Send(HttpMethod.Get, $"/api/schedules/{schedule.Id}/runs", user: "alice");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var history = await response.Content.ReadFromJsonAsync<List<RunDiagnosis>>();
        var controllerDiagnosis = history.Should().ContainSingle().Subject;

        // Path 2: the tool, over IScheduleDiagnostics resolved from the SAME composed host — the same interface,
        // not a second query path or a second classification.
        using var scope = _factory.Services.CreateScope();
        var diagnostics = scope.ServiceProvider.GetRequiredService<IScheduleDiagnostics>();
        var tool = new DaedalusScheduleTools(diagnostics);
        var toolOutput = await tool.WhyDidARunFail(schedule.Id, take: 5, ct: CancellationToken.None);

        // The controller side is the independently-verified ground truth (ScheduleDiagnosticsTests pins Failed's
        // precedence and fields); this asserts the TOOL's report is not a second opinion of it.
        controllerDiagnosis.Verdict.Should().Be(RunVerdict.Failed);
        controllerDiagnosis.FailedAtStep.Should().Be((int)RunStep.Writer);

        // A real comparison of both paths, not a separate assertion of an expected constant: the tool's text is
        // checked against the FIELDS the controller actually returned for this run, not against a hard-coded
        // "Writer"/"Failed" string that could pass even if the tool secretly used its own classification.
        toolOutput.Should().Contain(controllerDiagnosis.Verdict.ToString());
        toolOutput.Should().Contain(((RunStep)controllerDiagnosis.FailedAtStep!.Value).ToString());
        toolOutput.Should().Contain(controllerDiagnosis.LastError!);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string user)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderTestAuthHandler.UserHeader, user);
        return await _client.SendAsync(request);
    }
}
