namespace Daedalus.Tests.Playwright.Api.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("RalphConfig")]
[Description("Ralph Config API endpoint tests")]
public class RalphConfigApiTests : ApiTestBase
{
    [Test]
    [Description("GET /api/ralph-config should return config DTO")]
    public async Task RalphConfigApi_Get_ShouldReturnConfig()
    {
        var response = await GetApiResponseAsync("/api/ralph-config").ConfigureAwait(false);
        response.Ok.Should().BeTrue("GET /api/ralph-config should return success");
        response.Status.Should().Be(200);
        var body = await response.TextAsync().ConfigureAwait(false);
        body.Should().Contain("iterationDelayMs");
        body.Should().Contain("maxIterations");
        body.Should().Contain("promptOptions");
    }

    [Test]
    [Description("PUT /api/ralph-config should accept config update")]
    public async Task RalphConfigApi_Put_ShouldUpdateConfig()
    {
        var config = new
        {
            iterationDelayMs = 2000,
            maxConsecutiveFailures = 5,
            maxIterations = 25,
            requestTimeoutSeconds = 120,
            enableDetailedLogging = true,
            promptOptions = new
            {
                maxParallelSubagents = 3,
                includeGitWorkflow = true,
                includeTestInstructions = true,
                includeQualityGuards = true,
                includeSelfImprovement = false,
                includeCodingStandards = true,
                includeLoggingInstructions = false
            }
        };
        var response = await PutApiAsync("/api/ralph-config", config).ConfigureAwait(false);
        response.Ok.Should().BeTrue("PUT /api/ralph-config should return success");
    }
}
