using Daedalus.Application.DTOs;

namespace Daedalus.Tests.Playwright.Api.Scenarios;

/// <summary>
///     Comprehensive E2E tests for the Executions page using Playwright API requests.
///     Tests: task executions by task ID, by session ID, pagination, error handling.
///     Note: No executions are seeded, so most tests verify empty-state behavior.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Executions")]
public class ExecutionsPageTests : ApiTestBase
{
    [Test]
    [Description("User can navigate to the Executions page and see executions for a task")]
    public async Task ExecutionsPage_NavigateByTask_PageLoadsSuccessfully()
    {
        // Arrange
        var tasks = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var taskId = tasks!.Items[0].Id;

        // Act
        var result = await GetApiAsync<PagedResultDto<TaskExecutionDto>>(
                $"/api/taskexecutions/task/{taskId}")
            .ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull("Task executions API should respond");
    }

    [Test]
    [Description("Executions page shows empty state for task with no executions")]
    public async Task ExecutionsPage_NoExecutions_DisplaysEmptyStateForTask()
    {
        // Arrange
        var tasks = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var taskId = tasks!.Items[0].Id;

        // Act — no executions are seeded
        var result = await GetApiAsync<PagedResultDto<TaskExecutionDto>>(
                $"/api/taskexecutions/task/{taskId}")
            .ConfigureAwait(false);

        // Assert
        result!.Total.Should().Be(0, "No executions are seeded in fixture");
        result.Items.Should().BeEmpty();
    }

    [Test]
    [Description("Executions page shows empty state for session with no executions")]
    public async Task ExecutionsPage_NoExecutions_DisplaysEmptyStateForSession()
    {
        // Act — use random GUID since no sessions exist
        var result = await GetApiAsync<PagedResultDto<TaskExecutionDto>>(
                $"/api/taskexecutions/session/{Guid.NewGuid()}")
            .ConfigureAwait(false);

        // Assert
        result!.Total.Should().Be(0, "No executions for non-existent session");
        result.Items.Should().BeEmpty();
    }

    [Test]
    [Description("Executions by task pagination is respected")]
    public async Task ExecutionsPage_Pagination_RespectsParametersForTask()
    {
        // Arrange
        var tasks = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var taskId = tasks!.Items[0].Id;

        // Act
        var result = await GetApiAsync<PagedResultDto<TaskExecutionDto>>(
                $"/api/taskexecutions/task/{taskId}?page=1&pageSize=5")
            .ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull();
        result!.Page.Should().Be(1);
        result.PageSize.Should().Be(5);
    }

    [Test]
    [Description("Executions by session pagination is respected")]
    public async Task ExecutionsPage_Pagination_RespectsParametersForSession()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskExecutionDto>>(
                $"/api/taskexecutions/session/{Guid.NewGuid()}?page=1&pageSize=5")
            .ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull();
        result!.Page.Should().Be(1);
        result.PageSize.Should().Be(5);
    }

    [Test]
    [Description("Executions by task endpoint returns correct content type")]
    public async Task ExecutionsPage_ContentType_IsApplicationJson()
    {
        // Arrange
        var tasks = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var taskId = tasks!.Items[0].Id;

        // Act
        var response = await GetApiResponseAsync($"/api/taskexecutions/task/{taskId}")
            .ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue();
        response.Headers["content-type"].Should().Contain("application/json");
    }

    [Test]
    [Description("Executions page returns consistent results on refresh")]
    public async Task ExecutionsPage_Refresh_ReturnsConsistentResults()
    {
        // Arrange
        var tasks = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var taskId = tasks!.Items[0].Id;

        // Act
        var result1 = await GetApiAsync<PagedResultDto<TaskExecutionDto>>(
                $"/api/taskexecutions/task/{taskId}")
            .ConfigureAwait(false);
        var result2 = await GetApiAsync<PagedResultDto<TaskExecutionDto>>(
                $"/api/taskexecutions/task/{taskId}")
            .ConfigureAwait(false);

        // Assert
        result1!.Total.Should().Be(result2!.Total, "Execution count should be consistent");
    }

    [Test]
    [Description("Page 2 returns empty when no executions")]
    public async Task ExecutionsPage_Page2_ReturnsEmptyWhenNoData()
    {
        // Arrange
        var tasks = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var taskId = tasks!.Items[0].Id;

        // Act
        var result = await GetApiAsync<PagedResultDto<TaskExecutionDto>>(
                $"/api/taskexecutions/task/{taskId}?page=2&pageSize=10")
            .ConfigureAwait(false);

        // Assert
        result!.Items.Should().BeEmpty();
    }

    [Test]
    [Description("Executions for both seeded tasks return empty results")]
    public async Task ExecutionsPage_AllSeededTasks_HaveNoExecutions()
    {
        // Arrange
        var tasks = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);

        // Assert
        foreach (var task in tasks!.Items)
        {
            var result = await GetApiAsync<PagedResultDto<TaskExecutionDto>>(
                    $"/api/taskexecutions/task/{task.Id}")
                .ConfigureAwait(false);

            result!.Total.Should().Be(0, $"Task '{task.Id}' should have no executions");
        }
    }

    [Test]
    [Description("Session-based execution query returns correct paged structure")]
    public async Task ExecutionsPage_SessionQuery_ReturnsPagedStructure()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskExecutionDto>>(
                $"/api/taskexecutions/session/{Guid.NewGuid()}")
            .ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull();
        result!.Items.Should().NotBeNull();
        result.Page.Should().BeGreaterThanOrEqualTo(1);
        result.PageSize.Should().BeGreaterThan(0);
    }
}
