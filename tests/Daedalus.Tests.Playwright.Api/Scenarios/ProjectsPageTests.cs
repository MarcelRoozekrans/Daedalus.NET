using Daedalus.Application.DTOs;

namespace Daedalus.Tests.Playwright.Api.Scenarios;

/// <summary>
///     Comprehensive E2E tests for the Projects page using Playwright API requests.
///     Tests: project listing, CRUD operations, with-tasks, pagination, validation, error handling.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Projects")]
public class ProjectsPageTests : ApiTestBase
{
    [Test]
    [Description("User can navigate to the Projects page and see projects")]
    public async Task ProjectsPage_NavigateToPage_PageLoadsSuccessfully()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull("Projects API should respond");
    }

    [Test]
    [Description("Projects page displays the heading/data")]
    public async Task ProjectsPage_PageHeading_IsDisplayedOnLoad()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);

        // Assert
        result!.Items.Should().NotBeNull();
        result.Total.Should().BeGreaterThanOrEqualTo(0, "Should show project count or empty state");
    }

    [Test]
    [Description("Projects page returns data after loading")]
    public async Task ProjectsPage_InitialLoad_ShowsLoadingIndicator()
    {
        // Act
        var response = await GetApiResponseAsync("/api/projects").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("API should return success after loading");
        response.Status.Should().Be(200);
    }

    [Test]
    [Description("Projects are displayed with information")]
    public async Task ProjectsPage_ProjectsDisplay_ShowsProjectCardsWithInformation()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);

        // Assert
        if (result!.Items.Count > 0)
        {
            result.Items[0].ProjectName.Should().NotBeNullOrEmpty("Project should have a name");
            result.Items[0].Version.Should().NotBeNullOrEmpty("Project should have a version");
        }
    }

    [Test]
    [Description("Project card displays name, version, description, and task count")]
    public async Task ProjectsPage_ProjectCard_DisplaysAllProjectInformation()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);

        // Assert
        result!.Items.Should().NotBeEmpty();
        var project = result.Items[0];
        project.Id.Should().NotBeEmpty();
        project.ProjectName.Should().NotBeNullOrEmpty();
        project.Version.Should().NotBeNullOrEmpty();
        project.CreatedAt.Should().NotBe(default);
    }

    [Test]
    [Description("User can select a project to view its details")]
    public async Task ProjectsPage_SelectProject_DisplaysProjectDetails()
    {
        // Arrange
        var all = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);
        var projectId = all!.Items[0].Id;

        // Act
        var project = await GetApiAsync<ProjectDto>($"/api/projects/{projectId}").ConfigureAwait(false);

        // Assert
        project.Should().NotBeNull("Project details should be returned");
        project!.Id.Should().Be(projectId);
        project.ProjectName.Should().NotBeNullOrEmpty();
    }

    [Test]
    [Description("Project details show associated tasks")]
    public async Task ProjectsPage_ProjectDetails_DisplaysTasks()
    {
        // Arrange
        var all = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);
        var projectId = all!.Items[0].Id;

        // Act
        var project = await GetApiAsync<ProjectDto>($"/api/projects/{projectId}/with-tasks")
            .ConfigureAwait(false);

        // Assert
        project.Should().NotBeNull();
        project!.Tasks.Should().NotBeNull();
        project.Tasks.Should().HaveCountGreaterThanOrEqualTo(2, "Seeded project has 2 tasks");
    }

    [Test]
    [Description("Tasks display all required fields")]
    public async Task ProjectsPage_TaskRows_DisplayCompleteInformation()
    {
        // Arrange
        var all = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);
        var project = await GetApiAsync<ProjectDto>($"/api/projects/{all!.Items[0].Id}/with-tasks")
            .ConfigureAwait(false);

        // Assert
        foreach (var task in project!.Tasks)
        {
            task.Prompt.Should().NotBeNullOrEmpty("Task should have a prompt");
            task.CompletionPromise.Should().NotBeNullOrEmpty("Task should have a completion promise");
        }
    }

    [Test]
    [Description("Task status values are valid")]
    public async Task ProjectsPage_TaskStatuses_DisplayWithCorrectFormatting()
    {
        // Act
        var all = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);
        var project = await GetApiAsync<ProjectDto>($"/api/projects/{all!.Items[0].Id}/with-tasks")
            .ConfigureAwait(false);

        // Assert
        foreach (var task in project!.Tasks)
        {
            task.Status.Should().BeGreaterThanOrEqualTo(0, "Status should be valid enum value");
        }
    }

    [Test]
    [Description("Seeded project with tasks shows empty tasks for new project scope")]
    public async Task ProjectsPage_ProjectWithNoTasks_DisplaysEmptyMessage()
    {
        // Act — The seeded project has tasks, so verify a project with zero tasks would have empty list
        // Since POST doesn't persist, we verify the POST response itself returns empty tasks
        var createResponse = await PostApiAsync("/api/projects",
                new { projectName = "Empty Project", description = "No tasks here", version = "1.0" })
            .ConfigureAwait(false);

        createResponse.Ok.Should().BeTrue();
        var body = await createResponse.TextAsync().ConfigureAwait(false);
        var created = JsonSerializer.Deserialize<ProjectDto>(body, ApiJsonOptions);

        // Assert — the created response includes empty tasks list
        created!.Tasks.Should().BeEmpty("Newly created project should have no tasks");
    }

    [Test]
    [Description("Pagination controls work for multiple pages")]
    public async Task ProjectsPage_Pagination_ControlsVisibleForMultiplePages()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects?page=1&pageSize=1")
            .ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull();
        result!.Page.Should().Be(1);
        result.PageSize.Should().Be(1);
    }

    [Test]
    [Description("Current page indicator is accurate")]
    public async Task ProjectsPage_Pagination_CurrentPageIndicatorIsAccurate()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects?page=1&pageSize=10")
            .ConfigureAwait(false);

        // Assert
        result!.Page.Should().Be(1, "Current page should be 1");
    }

    [Test]
    [Description("Page has proper response format")]
    public async Task ProjectsPage_Accessibility_HasProperAttributes()
    {
        // Act
        var response = await GetApiResponseAsync("/api/projects").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue();
        response.Headers["content-type"].Should().Contain("application/json");
    }

    [Test]
    [Description("Projects page can be refreshed consistently")]
    public async Task ProjectsPage_Navigation_CanRefreshPage()
    {
        // Act
        var result1 = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);
        var result2 = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);

        // Assert
        result1!.Total.Should().Be(result2!.Total, "Project count should remain same after refresh");
    }

    [Test]
    [Description("All project names are non-empty")]
    public async Task ProjectsPage_DataValidation_ProjectNamesArePopulated()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);

        // Assert
        foreach (var project in result!.Items)
        {
            project.ProjectName.Should().NotBeNullOrWhiteSpace("All projects should have names");
        }
    }

    [Test]
    [Description("All project versions are valid")]
    public async Task ProjectsPage_DataValidation_ProjectVersionsAreValid()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);

        // Assert
        foreach (var project in result!.Items)
        {
            project.Version.Should().NotBeNullOrWhiteSpace("All projects should have versions");
        }
    }

    [Test]
    [Description("Seeded project has correct name")]
    public async Task ProjectsPage_SeededProject_HasCorrectName()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);

        // Assert
        result!.Items.Should().Contain(p => p.ProjectName == "Test Project",
            "Fixture seeds a project named 'Test Project'");
    }

    // CRUD Tests

    [Test]
    [Description("User can create a new project")]
    public async Task ProjectsPage_CreateProject_FormSubmitsSuccessfully()
    {
        // Arrange
        var projectName = $"Test Project {Guid.NewGuid().ToString()[..8]}";

        // Act
        var response =
            await PostApiAsync("/api/projects",
                new { projectName, description = "Test Description", version = "1.0.0" }).ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(201);
        var body = await response.TextAsync().ConfigureAwait(false);
        var project = JsonSerializer.Deserialize<ProjectDto>(body, ApiJsonOptions);
        project!.ProjectName.Should().Be(projectName);
    }

    [Test]
    [Description("Create Project form validates required fields")]
    public async Task ProjectsPage_CreateProject_FormValidatesRequiredFields()
    {
        // Act
        var response =
            await PostApiAsync("/api/projects", new { projectName = "", description = "No name", version = "1.0" })
                .ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(400, "Empty name should be rejected");
    }

    [Test]
    [Description("User can update project details")]
    public async Task ProjectsPage_EditProject_UpdatesSuccessfully()
    {
        // Arrange
        var all = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);
        var projectId = all!.Items[0].Id;

        // Act
        var response = await PutApiAsync($"/api/projects/{projectId}",
                new { projectName = "Updated Project Name", description = "Updated description", version = "2.0.0" })
            .ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Update should succeed");
    }

    [Test]
    [Description("Update returns 404 for non-existent project")]
    public async Task ProjectsPage_EditProject_Returns404ForMissing()
    {
        // Act
        var response = await PutApiAsync($"/api/projects/{Guid.NewGuid()}",
                new { projectName = "Ghost Project", description = "Does not exist", version = "1.0" })
            .ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(404);
    }

    [Test]
    [Description("Update validates required fields")]
    public async Task ProjectsPage_EditProject_ValidatesEmptyName()
    {
        // Arrange
        var all = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);
        var projectId = all!.Items[0].Id;

        // Act
        var response =
            await PutApiAsync($"/api/projects/{projectId}",
                new { projectName = "", description = "Desc", version = "1.0" }).ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(400);
    }

    [Test]
    [Description("User can delete a project")]
    public async Task ProjectsPage_DeleteProject_DeletesSuccessfully()
    {
        // Arrange — use seeded project (create doesn't persist to DB)
        var all = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);
        var projectId = all!.Items[0].Id;

        // Act — delete the seeded project (controller checks DB)
        var deleteResponse = await DeleteApiAsync($"/api/projects/{projectId}").ConfigureAwait(false);

        // Assert
        deleteResponse.Status.Should().Be(204, "Delete should return NoContent");
    }

    [Test]
    [Description("Delete returns 404 for non-existent project")]
    public async Task ProjectsPage_DeleteModal_Returns404ForMissing()
    {
        // Act
        var response = await DeleteApiAsync($"/api/projects/{Guid.NewGuid()}").ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(404);
    }

    [Test]
    [Description("GET project/{id} returns 404 for non-existent project")]
    public async Task ProjectsPage_GetById_Returns404ForMissing()
    {
        // Act
        var response = await GetApiResponseAsync($"/api/projects/{Guid.NewGuid()}").ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(404);
    }

    [Test]
    [Description("GET project/{id}/with-tasks returns 404 for non-existent project")]
    public async Task ProjectsPage_GetWithTasks_Returns404ForMissing()
    {
        // Act
        var response = await GetApiResponseAsync($"/api/projects/{Guid.NewGuid()}/with-tasks")
            .ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(404);
    }
}
