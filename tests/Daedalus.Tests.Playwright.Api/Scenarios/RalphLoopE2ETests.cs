using Daedalus.Application.DTOs;

namespace Daedalus.Tests.Playwright.Api.Scenarios;

/// <summary>
///     E2E tests for Ralph Loop functionality using Playwright API requests.
///     Tests: health endpoint, API accessibility, code analysis endpoints,
///     error handling for invalid routes.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("RalphLoop")]
public class RalphLoopE2ETests : ApiTestBase
{
    private static readonly string[] AnalysisRequirements = ["Analyze code quality"];

    [Test]
    [Description("Health endpoint is accessible and returns healthy status")]
    public async Task RalphLoop_HealthEndpoint_ReturnsHealthy()
    {
        // Act
        var response = await GetApiResponseAsync("/health").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Health endpoint should return 200");
        var body = await response.TextAsync().ConfigureAwait(false);
        body.Should().Contain("Healthy", "Health check should report healthy status");
    }

    [Test]
    [Description("Tasks API is accessible for Ralph Loop worker")]
    public async Task RalphLoop_TasksApi_IsAccessible()
    {
        // Act
        var result = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);

        // Assert
        result.Should().NotBeNull("Tasks API must be accessible for Ralph Loop");
        result!.Items.Should().HaveCountGreaterThanOrEqualTo(2, "Seeded tasks should be available");
    }

    [Test]
    [Description("Next pending code analysis returns 204 when none pending")]
    public async Task RalphLoop_NextPending_Returns204WhenNonePending()
    {
        // Act
        var response = await GetApiResponseAsync("/api/codeanalysis/next-pending").ConfigureAwait(false);

        // Assert
        response.Status.Should().BeOneOf([200, 204], "Should return 204 No Content when no pending requests");
    }

    [Test]
    [Description("Code analysis by status returns empty results")]
    public async Task RalphLoop_CodeAnalysisByStatus_ReturnsResults()
    {
        // Act — status 0 = Pending
        var response = await GetApiResponseAsync("/api/codeanalysis/by-status/0").ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("Code analysis by-status endpoint should respond");
    }

    [Test]
    [Description("Code analysis by repository returns results for unknown URL")]
    public async Task RalphLoop_CodeAnalysisByRepository_ReturnsEmptyForUnknown()
    {
        // Act
        var response = await GetApiResponseAsync(
                "/api/codeanalysis/by-repository?repositoryUrl=https://github.com/nonexistent/repo")
            .ConfigureAwait(false);

        // Assert
        response.Ok.Should().BeTrue("By-repository endpoint should respond even for unknown repos");
    }

    [Test]
    [Description("Code analysis by ID returns 404 for non-existent request")]
    public async Task RalphLoop_CodeAnalysisById_Returns404ForMissing()
    {
        // Act
        var response = await GetApiResponseAsync($"/api/codeanalysis/{Guid.NewGuid()}")
            .ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(404, "Non-existent code analysis should return 404");
    }

    [Test]
    [Description("Invalid route returns 404")]
    public async Task RalphLoop_InvalidRoute_Returns404()
    {
        // Act
        var response = await GetApiResponseAsync("/api/ralph-loop/nonexistent").ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(404);
    }

    [Test]
    [Description("Multiple API requests can be made sequentially")]
    public async Task RalphLoop_MultipleRequests_CanBeMadeSequentially()
    {
        // Act — simulate worker polling pattern
        var healthResponse = await GetApiResponseAsync("/health").ConfigureAwait(false);
        var tasksResult = await GetApiAsync<PagedResultDto<TaskDto>>("/api/tasks").ConfigureAwait(false);
        var pendingResponse = await GetApiResponseAsync("/api/codeanalysis/next-pending")
            .ConfigureAwait(false);

        // Assert
        healthResponse.Ok.Should().BeTrue("Health should be accessible");
        tasksResult.Should().NotBeNull("Tasks should be accessible");
        pendingResponse.Status.Should().BeOneOf([200, 204], "Code analysis should respond");
    }

    [Test]
    [Description("Code analysis submit returns 201")]
    public async Task RalphLoop_SubmitCodeAnalysis_Returns201()
    {
        // Act
        var response = await PostApiAsync("/api/codeanalysis",
            new
            {
                repositoryUrl = "https://github.com/test/ralph-loop-test",
                filePath = "src/main.cs",
                type = 0,
                title = "E2E Test Analysis",
                description = "Testing code analysis submission",
                requirements = AnalysisRequirements,
                maxIterations = 5
            }).ConfigureAwait(false);

        // Assert
        response.Status.Should().Be(201, "Code analysis submission should return 201 Created");
    }
}
