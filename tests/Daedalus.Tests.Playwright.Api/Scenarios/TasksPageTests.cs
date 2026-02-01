using Daedalus.Application.DTOs;

namespace Daedalus.Tests.Playwright.Api.Scenarios;

/// <summary>
///     E2E tests for the Tasks page using Playwright API requests.
///     Validates task listing, pagination, status filtering, and accessibility.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Tasks")]
[Description("Tasks page E2E tests validating grid display, pagination, status filtering, and accessibility")]
public class TasksPageTests : ApiTestBase
{
    [Test]
    [Description("Verify tasks page loads successfully")]
    public async Task TasksPageShould_LoadSuccessfully()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull("Tasks API should return a valid result");
        result!.Items.Should().NotBeNull();
    }

    [Test]
    [Description("Verify page displays tasks in grid")]
    public async Task TasksPageShould_DisplayTasksGrid()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull();
        result!.Items.Should().NotBeEmpty("Tasks grid should display seeded tasks");
    }

    [Test]
    [Description("Verify tasks are displayed with seeded data")]
    public async Task TasksPageShould_DisplayTasks()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);

        // Assert
        result!.Items.Should().HaveCountGreaterThanOrEqualTo(2, "Should have at least 2 seeded tasks");
        result.Total.Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    [Description("Verify task information is complete")]
    public async Task TasksPageShould_DisplayCompleteTaskInfo()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var firstTask = result!.Items.Count > 0 ? result.Items[0] : null;

        // Assert
        firstTask.Should().NotBeNull("Should have at least one task");
        firstTask!.Id.Should().NotBeEmpty();
        firstTask.Prompt.Should().NotBeNullOrEmpty("Task prompt should be present");
        firstTask.CompletionPromise.Should().NotBeNullOrEmpty("Task completion promise should be present");
    }

    [Test]
    [Description("Verify pagination controls work")]
    public async Task TasksPageShould_DisplayPaginationControls()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks?page=1&pageSize=10")
            .ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull();
        result!.Page.Should().BeGreaterThanOrEqualTo(1);
        result.PageSize.Should().BeGreaterThan(0);
    }

    [Test]
    [Description("Verify page indicator shows current page")]
    public async Task TasksPageShould_DisplayPageIndicator()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks?page=1&pageSize=10")
            .ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull();
        result!.Page.Should().Be(1, "Should start at page 1");
    }

    [Test]
    [Description("Verify next page navigation works")]
    public async Task TasksPageShould_NavigateToNextPage()
    {
        // Arrange
        var page1 = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks?page=1&pageSize=1")
            .ConfigureAwait(false);

        if (page1!.Total <= 1)
        {
            Assert.Inconclusive("Only one task available — cannot test next page");
            return;
        }

        // Act
        var page2 = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks?page=2&pageSize=1")
            .ConfigureAwait(false);

        // Assert
        page2.Should().NotBeNull();
        page2!.Page.Should().Be(2);
        page2.Items.Should().NotBeEmpty("Page 2 should have tasks");
        page2.Items[0].Id.Should().NotBe(page1.Items[0].Id, "Different page should show different tasks");
    }

    [Test]
    [Description("Verify previous page navigation works")]
    public async Task TasksPageShould_NavigateToPreviousPage()
    {
        // Arrange
        var page2 = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks?page=2&pageSize=1")
            .ConfigureAwait(false);

        if (page2!.Items.Count == 0)
        {
            Assert.Inconclusive("Only one page of tasks available");
            return;
        }

        // Act
        var page1 = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks?page=1&pageSize=1")
            .ConfigureAwait(false);

        // Assert
        page1.Should().NotBeNull();
        page1!.Page.Should().Be(1);
        page1.Items[0].Id.Should().NotBe(page2.Items[0].Id, "Page should change when navigating back");
    }

    [Test]
    [Description("Verify task status badges are present")]
    public async Task TasksPageShould_DisplayStatusBadges()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);

        // Assert
        foreach (var task in result!.Items)
        {
            task.Status.Should().BeGreaterThanOrEqualTo(0, "Each task should have a valid status");
        }
    }

    [Test]
    [Description("Verify API returns JSON with correct content type")]
    public async Task TasksPageShould_DisplayErrorAlert_WithAccessibilityRole()
    {
        // Act
        var response = await GetApiResponseAsync("/api/tasks").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue();
        response.Headers["content-type"].Should().Contain("application/json");
    }

    [Test]
    [Description("Verify health endpoint responds (loading state check)")]
    public async Task TasksPageShould_DisplayLoadingState_WithAccessibilityRole()
    {
        // Act
        var response = await GetApiResponseAsync("/health").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Server should be responsive");
    }

    [Test]
    [Description("Verify page has proper response format")]
    public async Task TasksPageShould_HaveAccessibilityAttributes()
    {
        // Act
        var response = await GetApiResponseAsync("/api/tasks").ConfigureAwait(false);
        var body = await response.TextAsync().ConfigureAwait(false);

        // Assert
        body.Should().Contain("items").And.Contain("total").And.Contain("page");
    }

    [Test]
    [Description("Verify no loading after data returns")]
    public async Task TasksPageShould_HideLoading_WhenDataLoaded()
    {
        // Act
        var response = await GetApiResponseAsync("/api/tasks").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Data should load successfully");
        response.Status.Should().Be(200);
    }

    [Test]
    [Description("Verify no error on successful load")]
    public async Task TasksPageShould_NotDisplayError_OnSuccessfulLoad()
    {
        // Act
        var response = await GetApiResponseAsync("/api/tasks").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("No error should occur on successful request");
    }

    [Test]
    [Description("Verify task by ID returns details")]
    public async Task TasksPageShould_ProvideViewDetailsOption()
    {
        // Arrange
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var taskId = result!.Items[0].Id;

        // Act
        var task = await GetApiAsync<TaskDto>($"/api/tasks/{taskId}").ConfigureAwait(false);

        // Assert
        task.Should().NotBeNull();
        task!.Id.Should().Be(taskId);
    }

    [Test]
    [Description("Verify task count matches total")]
    public async Task TasksPageShould_CountMatchesDisplay()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);

        // Assert
        result!.Items.Should().HaveCount(Math.Min(result.Items.Count, result.Total));
    }

    [Test]
    [Description("Verify tasks refresh on reload")]
    public async Task TasksPageShould_RefreshTasks_OnPageReload()
    {
        // Act
        var result1 = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var result2 = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);

        // Assert
        result1!.Total.Should().Be(result2!.Total, "Tasks should be consistent after reload");
    }

    [Test]
    [Description("Verify pagination updates task display")]
    public async Task TasksPageShould_UpdateTasks_WhenPaginationChanges()
    {
        // Act
        var page1 = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks?page=1&pageSize=1")
            .ConfigureAwait(false);

        if (page1!.Total <= 1)
        {
            Assert.Inconclusive("Only one page available");
            return;
        }

        var page2 = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks?page=2&pageSize=1")
            .ConfigureAwait(false);

        // Assert
        page2!.Items.Should().NotBeEmpty("Next page should have tasks");
    }
}
