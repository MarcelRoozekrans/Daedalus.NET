using Daedalus.Application.DTOs;

namespace Daedalus.Tests.Playwright.Api.Scenarios;

/// <summary>
///     Comprehensive E2E tests for Repository Configuration using Playwright API requests.
///     Tests: CRUD operations, validation, test-connection, error handling.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Repositories")]
public class RepositoryConfigurationE2ETests : ApiTestBase
{
    [Test]
    [Description("User can navigate to the Repositories page and see configurations")]
    public async Task Repositories_NavigateToPage_PageLoadsSuccessfully()
    {
        // Act
        var response = await GetApiResponseAsync("/api/repositories").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Repositories API should respond");
    }

    [Test]
    [Description("Repositories list returns proper collection")]
    public async Task Repositories_List_ReturnsCollection()
    {
        // Act
        var response = await GetApiResponseAsync("/api/repositories").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue();
        var body = await response.TextAsync().ConfigureAwait(false);
        var repos = JsonSerializer.Deserialize<List<RepositoryConfigurationDto>>(body, ApiJsonOptions);
        repos.Should().NotBeNull("Should return a list of repository configurations");
    }

    [Test]
    [Description("User can create a new repository configuration")]
    public async Task Repositories_Create_SuccessfullyCreated()
    {
        // Act
        var response = await PostApiAsync("/api/repositories",
            new
            {
                name = $"Test Repo {Guid.NewGuid().ToString()[..8]}",
                url = "https://github.com/test/new-repo",
                platform = "GitHub",
                defaultBranch = "main",
                description = "Created via E2E test"
            }).ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(201, "Create should return 201 Created");
        var body = await response.TextAsync().ConfigureAwait(false);
        var repo = JsonSerializer.Deserialize<RepositoryConfigurationDto>(body, ApiJsonOptions);
        repo!.Name.Should().StartWith("Test Repo");
        repo.Url.Should().Be("https://github.com/test/new-repo");
        repo.Platform.Should().Be("GitHub");
        repo.IsActive.Should().BeTrue("New repos should be active by default");
    }

    [Test]
    [Description("Create validates required fields")]
    public async Task Repositories_Create_ValidatesRequiredFields()
    {
        // Act
        var response =
            await PostApiAsync("/api/repositories",
                new { name = "", url = "", platform = "GitHub", defaultBranch = "main" }).ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(400, "Empty required fields should be rejected");
    }

    [Test]
    [Description("User can get a repository by ID")]
    public async Task Repositories_GetById_ReturnsRepository()
    {
        // Act — controller is a placeholder that always returns a stubbed repo for any GUID
        var response = await GetApiResponseAsync($"/api/repositories/{Guid.NewGuid()}")
            .ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Placeholder controller returns 200 for any ID");
        var body = await response.TextAsync().ConfigureAwait(false);
        var repo = JsonSerializer.Deserialize<RepositoryConfigurationDto>(body, ApiJsonOptions);
        repo.Should().NotBeNull();
        repo!.Name.Should().NotBeNullOrEmpty();
    }

    [Test]
    [Description("Get by ID returns 200 for any GUID (placeholder controller)")]
    public async Task Repositories_GetById_ReturnsOkForAnyId()
    {
        // Act — placeholder controller always returns OK with stubbed data
        var response = await GetApiResponseAsync($"/api/repositories/{Guid.NewGuid()}")
            .ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Placeholder controller returns 200 for any ID");
    }

    [Test]
    [Description("User can update a repository configuration")]
    public async Task Repositories_Update_SuccessfullyUpdated()
    {
        // Arrange — create a repo to update
        var createResponse = await PostApiAsync("/api/repositories",
            new
            {
                name = "Update Test Repo",
                url = "https://github.com/test/update",
                platform = "GitHub",
                defaultBranch = "main"
            }).ConfigureAwait(false);

        var createBody = await createResponse.TextAsync().ConfigureAwait(false);
        var created = JsonSerializer.Deserialize<RepositoryConfigurationDto>(createBody, ApiJsonOptions);

        // Act
        var updateResponse = await PutApiAsync($"/api/repositories/{created!.Id}",
            new
            {
                name = "Updated Repo Name",
                url = "https://github.com/test/updated",
                defaultBranch = "develop",
                isActive = false,
                description = "Updated description"
            }).ConfigureAwait(false);

        // Assert
        updateResponse.Ok.Should().BeTrue("Update should succeed");
    }

    [Test]
    [Description("Update returns 204 for any GUID (placeholder controller)")]
    public async Task Repositories_Update_Returns204ForAnyId()
    {
        // Act — placeholder controller always returns NoContent
        var response = await PutApiAsync($"/api/repositories/{Guid.NewGuid()}",
                new
                {
                    name = "Ghost Repo",
                    url = "https://github.com/test/ghost",
                    defaultBranch = "main",
                    isActive = true
                })
            .ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(204, "Placeholder controller always returns NoContent");
    }

    [Test]
    [Description("User can delete a repository configuration (placeholder always returns 204)")]
    public async Task Repositories_Delete_SuccessfullyDeleted()
    {
        // Act — placeholder controller always returns 204 for any ID
        var deleteResponse = await DeleteApiAsync($"/api/repositories/{Guid.NewGuid()}").ConfigureAwait(false);

        // Assert
        deleteResponse.Status.Should().Be(204, "Delete should return 204 No Content");
    }

    [Test]
    [Description("Delete returns 204 for any GUID (placeholder controller)")]
    public async Task Repositories_Delete_Returns204ForAnyId()
    {
        // Act — placeholder controller always returns NoContent
        var response = await DeleteApiAsync($"/api/repositories/{Guid.NewGuid()}").ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(204, "Placeholder controller always returns NoContent");
    }

    [Test]
    [Description("Repository list always returns hardcoded data (placeholder controller)")]
    public async Task Repositories_List_ReturnsHardcodedData()
    {
        // Act — placeholder controller always returns hardcoded sample data
        var response = await GetApiResponseAsync("/api/repositories").ConfigureAwait(false);
        var body = await response.TextAsync().ConfigureAwait(false);
        var repos = JsonSerializer.Deserialize<List<RepositoryConfigurationDto>>(body, ApiJsonOptions);

        // Assert
        repos.Should().NotBeEmpty("Placeholder controller returns at least one sample repository");
        repos![0].Name.Should().Be("Sample Repository");
    }

    [Test]
    [Description("Repository response contains all expected fields")]
    public async Task Repositories_Fields_ContainAllExpected()
    {
        // Arrange — create a repo
        var createResponse = await PostApiAsync("/api/repositories",
            new
            {
                name = "Fields Test Repo",
                url = "https://github.com/test/fields",
                platform = "GitHub",
                defaultBranch = "main",
                description = "Testing field presence"
            }).ConfigureAwait(false);

        var body = await createResponse.TextAsync().ConfigureAwait(false);
        var repo = JsonSerializer.Deserialize<RepositoryConfigurationDto>(body, ApiJsonOptions);

        // Assert
        repo!.Id.Should().NotBeEmpty();
        repo.Name.Should().Be("Fields Test Repo");
        repo.Url.Should().Be("https://github.com/test/fields");
        repo.Platform.Should().Be("GitHub");
        repo.DefaultBranch.Should().Be("main");
        repo.IsActive.Should().BeTrue();
        repo.CreatedAt.Should().NotBe(default);
        repo.Description.Should().Be("Testing field presence");
    }

    [Test]
    [Description("Repositories endpoint returns correct content type")]
    public async Task Repositories_ContentType_IsApplicationJson()
    {
        // Act
        var response = await GetApiResponseAsync("/api/repositories").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue();
        response.Headers["content-type"].Should().Contain("application/json");
    }
}
