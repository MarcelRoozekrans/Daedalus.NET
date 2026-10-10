using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Thalos;
using Task = System.Threading.Tasks.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     Amendment A2 over HTTP: on the default test host the workflow engine is off and <c>WorkflowRunGateway</c> is not
///     registered, yet a task with a run still reads, showing its stored status.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TaskStatusOnDisabledEngineTests(PostgresFixture fixture) : IAsyncLifetime
{
    private ApiWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    ///     Red: inject <c>WorkflowRunGateway</c> into <c>TaskQueryService</c>; the GET is 500.
    ///     Red: map an unknown run to <c>Pending</c>; the stored <c>Failed</c> reads as 0.
    ///     Red: leave <c>WorkflowRunId</c> out of the mapper; it reads null.
    /// </summary>
    [Fact]
    public async Task A_task_with_a_run_shows_its_stored_status_when_the_engine_is_off()
    {
        var project = IntegrationTestFactory.CreateProject();
        var task = IntegrationTestFactory.CreateTask(projectId: project.Id);
        task.AttachRun(Guid.NewGuid()).IsSuccess.Should().BeTrue();
        project.AddTask(task).IsSuccess.Should().BeTrue();
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            db.Projects.Add(project);
            await db.SaveChangesAsync();
        }

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"UPDATE \"Tasks\" SET \"Status\" = 3 WHERE \"Id\" = '{task.Id}'", connection);
            await command.ExecuteNonQueryAsync();
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, "a-reader");
        var dto = await client.GetFromJsonAsync<TaskDto>($"/api/tasks/{task.Id}");

        dto!.Status.Should().Be((int)TaskStatus.Failed);
        dto.WorkflowRunId.Should().Be(task.WorkflowRunId);
    }
}
