using System.Diagnostics;

namespace Daedalus.Tests.Playwright.Api.Scenarios;

/// <summary>
///     E2E tests for application navigation and routing using Playwright API requests.
///     Tests: route accessibility, 404 handling, health endpoint, API discoverability.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Navigation")]
public class NavigationTests : ApiTestBase
{
    [Test]
    [Description("Health endpoint is accessible")]
    public async Task Navigation_HealthEndpoint_IsAccessible()
    {
        // Act
        var response = await GetApiResponseAsync("/health").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Health endpoint should return 200");
    }

    [Test]
    [Description("Tasks API route is accessible")]
    public async Task Navigation_TasksRoute_IsAccessible()
    {
        // Act
        var response = await GetApiResponseAsync("/api/tasks").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Tasks API should be accessible");
    }

    [Test]
    [Description("Projects API route is accessible")]
    public async Task Navigation_ProjectsRoute_IsAccessible()
    {
        // Act
        var response = await GetApiResponseAsync("/api/projects").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Projects API should be accessible");
    }

    [Test]
    [Description("Sessions API route is accessible")]
    public async Task Navigation_SessionsRoute_IsAccessible()
    {
        // Act
        var response = await GetApiResponseAsync("/api/executionsessions").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Sessions API should be accessible");
    }

    [Test]
    [Description("Repositories API route is accessible")]
    public async Task Navigation_RepositoriesRoute_IsAccessible()
    {
        // Act
        var response = await GetApiResponseAsync("/api/repositories").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Repositories API should be accessible");
    }

    [Test]
    [Description("Code Analysis API route is accessible")]
    public async Task Navigation_CodeAnalysisRoute_IsAccessible()
    {
        // Act — next-pending returns 204 when none pending
        var response = await GetApiResponseAsync("/api/codeanalysis/next-pending").ConfigureAwait(false);

        // Assert
        response.Status.Should().BeOneOf([200, 204], "Code analysis endpoint should respond");
    }

    [Test]
    [Description("Non-existent API route returns 404")]
    public async Task Navigation_NonExistentRoute_Returns404()
    {
        // Act
        var response = await GetApiResponseAsync("/api/nonexistent-endpoint").ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(404, "Non-existent routes should return 404");
    }

    [Test]
    [Description("Non-existent nested API route returns 404")]
    public async Task Navigation_NonExistentNestedRoute_Returns404()
    {
        // Act
        var response = await GetApiResponseAsync("/api/tasks/nonexistent/deep/path")
            .ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(404);
    }

    [Test]
    [Description("All main API routes respond within acceptable time")]
    public async Task Navigation_ApiRoutes_RespondQuickly()
    {
        var routes = new[] { "/health", "/api/tasks", "/api/projects", "/api/executionsessions", "/api/repositories" };

        foreach (var route in routes)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await GetApiResponseAsync(route).ConfigureAwait(false);
            stopwatch.Stop();

            response.Ok.Should().BeTrue($"Route {route} should return 200");
            stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000,
                $"Route {route} should respond within 5 seconds");
        }
    }

    [Test]
    [Description("API routes return correct content types")]
    public async Task Navigation_ApiRoutes_ReturnJsonContentType()
    {
        var jsonRoutes = new[] { "/api/tasks", "/api/projects", "/api/executionsessions", "/api/repositories" };

        foreach (var route in jsonRoutes)
        {
            var response = await GetApiResponseAsync(route).ConfigureAwait(false);
            response.Headers["content-type"].Should().Contain("application/json",
                $"Route {route} should return JSON content type");
        }
    }
}
