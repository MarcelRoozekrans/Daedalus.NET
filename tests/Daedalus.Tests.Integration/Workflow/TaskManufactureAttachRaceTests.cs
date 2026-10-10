using System.Text.Json;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Workflow;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     The attach of a started run is saved on the task instance the service read, so the task's <c>xmin</c> guards it.
///     Here the real starter starts a real run, and another request renames the task before the attach. The attach is
///     refused, the orphaned run is cancelled, and the endpoint answers 409 saying so. No second run is started.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TaskManufactureAttachRaceTests(PostgresFixture fixture)
{
    /// <summary>
    ///     Red: in <c>TaskManufactureService.StartAsync</c>, re-read the task through <c>GetByIdAsync</c> after the start
    ///     and attach the run to that fresh copy; the attach is saved, so the POST answers 201.
    ///     Red: skip the cancel of the unattached run; the run is not Cancelled and <c>runCancelled</c> is false.
    /// </summary>
    [Fact]
    public async Task A_task_changed_while_its_run_starts_refuses_the_attach_and_cancels_the_run()
    {
        RenamingStarter? renaming = null;
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            new ScriptedManufactureRuntime(),
            configureServices: services =>
            {
                var real = services.Last(d => d.ServiceType == typeof(IManufactureRunStarter));
                services.Remove(real);
                services.AddSingleton<IManufactureRunStarter>(sp =>
                    renaming = new RenamingStarter(
                        (IManufactureRunStarter)real.ImplementationFactory!(sp), sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()));
            });

        var project = Project.Create(Guid.NewGuid(), "Sandbox", "The scratch remote", repositoryUrl: host.Remote.Url).Value;
        var task = IntegrationTestFactory.CreateTask(projectId: project.Id);
        project.AddTask(task).IsSuccess.Should().BeTrue();
        await using (var db = await host.Factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            db.Projects.Add(project);
            await db.SaveChangesAsync();
        }

        using var client = host.Client("a-developer", "developer");
        var response = await client.PostAsync($"/api/tasks/{task.Id}/manufacture", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        renaming.Should().NotBeNull();
        renaming!.Started.Should().ContainSingle("the lost race must not start a second run");
        var runId = renaming.Started[0];
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("runId").GetGuid().Should().Be(runId);
        body.RootElement.GetProperty("runCancelled").GetBoolean().Should().BeTrue();

        var run = await host.Store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Cancelled, "a run no task points at must not keep running");

        await using var check = await host.Factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        var stored = await check.Tasks.SingleAsync(t => t.Id == task.Id);
        stored.WorkflowRunId.Should().BeNull("the lost attach must not be saved");
        stored.Title.Should().Be("Renamed", "the other request's change stands");
    }

    /// <summary>The real starter, after which another request renames every task the start was for.</summary>
    private sealed class RenamingStarter(IManufactureRunStarter inner, IDbContextFactory<ApplicationDbContext> contexts) : IManufactureRunStarter
    {
        public List<Guid> Started { get; } = [];

        public async ValueTask<Result<Guid, ManufactureStartFailure>> StartAsync(ManufactureStartRequest request, CancellationToken ct)
        {
            var started = await inner.StartAsync(request, ct);
            if (started.IsSuccess)
            {
                Started.Add(started.Value);
                await using var other = await contexts.CreateDbContextAsync(ct);
                foreach (var task in await other.Tasks.ToListAsync(ct))
                {
                    task.UpdateMetadata("Renamed", "d", Priority.Low, "p", Complexity.Low).IsSuccess.Should().BeTrue();
                }

                await other.SaveChangesAsync(ct);
            }

            return started;
        }
    }
}
