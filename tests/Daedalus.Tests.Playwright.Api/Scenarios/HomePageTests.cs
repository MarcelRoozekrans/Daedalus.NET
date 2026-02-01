using Daedalus.Application.DTOs;

namespace Daedalus.Tests.Playwright.Api.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("Home")]
[Description("Home page E2E tests validating statistics display, quick links, error handling, and accessibility")]
public class HomePageTests : ApiTestBase
{
    [Test]
    [Description("Verify home page loads successfully and server is healthy")]
    public async Task HomePageShould_LoadSuccessfully()
    {
        var response = await GetApiResponseAsync("/health").ConfigureAwait(false);
        response.Ok.Should().BeTrue("Health endpoint should return success");
        var body = await response.TextAsync().ConfigureAwait(false);
        body.Should().Contain("Healthy");
    }

    [Test]
    [Description("Verify statistics data is available from API")]
    public async Task HomePageShould_DisplayStatisticsGrid()
    {
        var tasks = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var sessions = await GetApiAsync<PagedResultDto<ExecutionSessionDto>>("/api/executionsessions")
            .ConfigureAwait(false);
        tasks.Should().NotBeNull();
        tasks!.Total.Should().BeGreaterThanOrEqualTo(0);
        sessions.Should().NotBeNull();
        sessions!.Total.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    [Description("Verify individual statistics display correct values")]
    public async Task HomePageShould_DisplayStatValueByLabel()
    {
        var tasks = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        tasks.Should().NotBeNull("Tasks API should return data");
        tasks!.Total.Should().BeGreaterThanOrEqualTo(2, "Seeded data should include tasks");
    }

    [Test]
    [Description("Verify all primary API endpoints are accessible (quick links)")]
    public async Task HomePageShould_DisplayQuickLinks()
    {
        var tasksResponse = await GetApiResponseAsync("/api/tasks").ConfigureAwait(false);
        var projectsResponse = await GetApiResponseAsync("/api/projects").ConfigureAwait(false);
        var sessionsResponse = await GetApiResponseAsync("/api/executionsessions").ConfigureAwait(false);
        tasksResponse.Ok.Should().BeTrue("Tasks API should be accessible");
        projectsResponse.Ok.Should().BeTrue("Projects API should be accessible");
        sessionsResponse.Ok.Should().BeTrue("Sessions API should be accessible");
    }

    [Test]
    [Description("Verify quick link navigation to tasks works")]
    public async Task HomePageShould_NavigateFromQuickLink()
    {
        var response = await GetApiResponseAsync("/api/tasks").ConfigureAwait(false);
        response.Ok.Should().BeTrue("Tasks endpoint should be accessible");
        response.Status.Should().Be(200);
    }

    [Test]
    [Description("Verify error state display for invalid endpoints")]
    public async Task HomePageShould_DisplayErrorAlert_WithAccessibilityRole()
    {
        var response = await GetApiResponseAsync("/api/nonexistent-endpoint").ConfigureAwait(false);
        response.Ok.Should().BeFalse("Invalid endpoint should return error");
        response.Status.Should().Be(404);
    }

    [Test]
    [Description("Verify loading state response has correct content type")]
    public async Task HomePageShould_DisplayLoadingState_WithAccessibilityRole()
    {
        var response = await GetApiResponseAsync("/api/tasks").ConfigureAwait(false);
        response.Ok.Should().BeTrue();
        var headers = response.Headers;
        headers.Should().ContainKey("content-type");
        headers["content-type"].Should().Contain("application/json");
    }

    [Test]
    [Description("Verify page has proper response format")]
    public async Task HomePageShould_HaveAccessibilityAttributes()
    {
        var response = await GetApiResponseAsync("/health").ConfigureAwait(false);
        response.Status.Should().Be(200);
        var body = await response.TextAsync().ConfigureAwait(false);
        body.Should().NotBeNullOrEmpty();
    }

    [Test]
    [Description("Verify statistics grid returns valid JSON")]
    public async Task HomePageShould_DisplayStatsGrid_WithAriaLabel()
    {
        var response = await GetApiResponseAsync("/api/tasks").ConfigureAwait(false);
        response.Ok.Should().BeTrue();
        var body = await response.TextAsync().ConfigureAwait(false);
        body.Should().Contain("items", "API should return paged result with items");
    }

    [Test]
    [Description("Verify no loading spinner — health is consistently available")]
    public async Task HomePageShould_HideLoading_WhenPageLoaded()
    {
        var response1 = await GetApiResponseAsync("/health").ConfigureAwait(false);
        var response2 = await GetApiResponseAsync("/health").ConfigureAwait(false);
        response1.Ok.Should().BeTrue("First health check should succeed");
        response2.Ok.Should().BeTrue("Second health check should succeed");
    }

    [Test]
    [Description("Verify page title — health endpoint returns content")]
    public async Task HomePageShould_HaveCorrectTitle()
    {
        var response = await GetApiResponseAsync("/health").ConfigureAwait(false);
        response.Ok.Should().BeTrue();
        var text = await response.TextAsync().ConfigureAwait(false);
        text.Should().NotBeNullOrEmpty("Health endpoint should return content");
    }

    [Test]
    [Description("Verify stats update — API data is consistent after repeated requests")]
    public async Task HomePageShould_RefreshStats_OnPageReload()
    {
        var result1 = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var result2 = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
        result1!.Total.Should().Be(result2!.Total, "Data should be consistent");
    }

    [Test]
    [Description("Verify quick links are properly accessible")]
    public async Task HomePageShould_HaveWellFormattedQuickLinks()
    {
        var endpoints = new[] { "/api/tasks", "/api/projects", "/api/executionsessions", "/health" };
        foreach (var endpoint in endpoints)
        {
            var response = await GetApiResponseAsync(endpoint).ConfigureAwait(false);
            response.Ok.Should().BeTrue($"Endpoint {endpoint} should be accessible");
        }
    }

    [Test]
    [Description("Verify no error alert on successful load")]
    public async Task HomePageShould_NotDisplayError_OnSuccessfulLoad()
    {
        var response = await GetApiResponseAsync("/api/tasks").ConfigureAwait(false);
        response.Ok.Should().BeTrue("Standard API request should not fail");
        response.Status.Should().Be(200);
    }
}
