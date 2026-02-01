using Daedalus.Application.DTOs;

namespace Daedalus.Tests.Playwright.Api.Scenarios;

/// <summary>
///     E2E tests for UI interaction patterns using Playwright API requests.
///     Tests: API response formats, JSON structure, headers, data consistency.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("UIInteraction")]
public class UIInteractionTests : ApiTestBase
{
    [Test]
    [Description("API response includes total count for client-side rendering")]
    public async Task UIInteraction_TasksResponse_IncludesTotalCountForPaging()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);

        // Assert
        result!.Total.Should().BeGreaterThan(0, "Total should indicate item count for UI rendering");
        result.Items.Count.Should().BeLessThanOrEqualTo(result.Total);
    }

    [Test]
    [Description("API response page size matches requested parameter")]
    public async Task UIInteraction_PageSize_MatchesRequestedParameter()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks?page=1&pageSize=1")
            .ConfigureAwait(false);

        // Assert
        result!.PageSize.Should().Be(1, "PageSize should match requested value");
        result.Items.Should().HaveCount(1);
    }

    [Test]
    [Description("Resource creation returns the new resource for client-side update")]
    public async Task UIInteraction_CreateResource_ReturnsCreatedResource()
    {
        // Act — use projects endpoint which supports POST
        var response = await PostApiAsync("/api/projects",
            new
            {
                projectName = $"UI Create Test {Guid.NewGuid().ToString()[..6]}",
                description = "Created for UI interaction test",
                version = "1.0"
            }).ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(201, "Create should return 201 Created");
        var body = await response.TextAsync().ConfigureAwait(false);
        var project = JsonSerializer.Deserialize<ProjectDto>(body, ApiJsonOptions);
        project.Should().NotBeNull("Response should contain the created resource for client update");
        project!.Description.Should().Be("Created for UI interaction test");
    }

    [Test]
    [Description("Project update returns OK with updated data")]
    public async Task UIInteraction_UpdateProject_ReturnsUpdatedData()
    {
        // Arrange
        var projects = await GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects").ConfigureAwait(false);
        var projectId = projects!.Items[0].Id;

        var newName = $"Updated-{Guid.NewGuid().ToString()[..6]}";

        // Act - update returns the updated DTO
        var updateResponse = await PutApiAsync($"/api/projects/{projectId}",
            new { projectName = newName, description = "Updated for UI test", version = "2.0" }).ConfigureAwait(false);

        // Assert
        updateResponse.Ok.Should().BeTrue("Update should return 200 OK");
        var body = await updateResponse.TextAsync().ConfigureAwait(false);
        var updated = JsonSerializer.Deserialize<ProjectDto>(body, ApiJsonOptions);
        updated!.ProjectName.Should().Be(newName, "Update response should contain new name");
    }

    [Test]
    [Description("Delete operation returns success")]
    public async Task UIInteraction_DeleteResource_ReturnsNoContent()
    {
        // Act — repositories delete is a placeholder that always returns 204
        var deleteResponse = await DeleteApiAsync($"/api/repositories/{Guid.NewGuid()}").ConfigureAwait(false);

        // Assert
        deleteResponse.Status.Should().Be(204, "Delete should return 204 No Content");
    }

    [Test]
    [Description("API response headers include application/json content type")]
    public async Task UIInteraction_ResponseHeaders_ContainProperContentType()
    {
        // Act
        var response = await GetApiResponseAsync("/api/tasks").ConfigureAwait(false);

        // Assert
        response.Headers["content-type"].Should().Contain("application/json",
            "API should return JSON for client-side parsing");
    }

    [Test]
    [Description("Empty collections return proper paged structure")]
    public async Task UIInteraction_EmptyCollections_ReturnProperPagedStructure()
    {
        // Act — sessions are empty (no seeds)
        var result = await GetApiAsync<PagedResultDto<ExecutionSessionDto>>("/api/executionsessions")
            .ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull("Empty collections should still return paged wrapper");
        result!.Items.Should().NotBeNull("Items collection should exist even when empty");
        result.Total.Should().Be(0);
        result.Page.Should().BeGreaterThanOrEqualTo(1);
        result.PageSize.Should().BeGreaterThan(0);
    }

    [Test]
    [Description("Multiple parallel requests return consistent data")]
    public async Task UIInteraction_ParallelRequests_ReturnConsistentData()
    {
        // Act
        var task1 = GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks");
        var task2 = GetApiAsync<PagedResultDto<ProjectDto>>("/api/projects");

        await Task.WhenAll(task1, task2).ConfigureAwait(false);

        // Assert
        var tasks = await task1.ConfigureAwait(false);
        var projects = await task2.ConfigureAwait(false);

        tasks.Should().NotBeNull();
        projects.Should().NotBeNull();
        tasks!.Total.Should().BeGreaterThan(0);
        projects!.Total.Should().BeGreaterThan(0);
    }
}
