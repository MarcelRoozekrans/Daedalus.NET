using System.Text.RegularExpressions;
using Daedalus.Tests.Playwright.Browser.Fixtures;
using Daedalus.Tests.Playwright.Browser.PageObjects;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     Drives the Thalos chat page end to end against the E2E server: real API, real Postgres session store, scripted
///     <see cref="StubAgentRuntime"/> (no model, no MCP). Covers the streaming path the unit tests cannot: SSE → Blazor
///     WASM rendering of text deltas, tool cards, usage and the composer state machine.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Agent")]
[Description("Agent chat page browser tests: sessions, streamed turn with tool card, composer state")]
public class AgentPageBrowserTests : BrowserTestBase
{
    private AgentPage _agentPage = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted) return;
        _agentPage = new AgentPage(Page, BaseUrl);
    }

    [Test]
    [Description("API preflight: the agents endpoint the page loads first lists the configured agent")]
    public async Task AgentsApi_ShouldList_ConfiguredAgent()
    {
        var response = await GetApiResponseAsync("/api/agents").ConfigureAwait(false);
        var body = await response.TextAsync().ConfigureAwait(false);

        response.Status.Should().Be(200, body);
        body.Should().Contain("Daedalus Architect");
    }

    [Test]
    [Description("Agent page should render the agent picker with the configured agent preselected")]
    public async Task AgentPage_ShouldRender_AgentPickerAndNewSession()
    {
        await _agentPage.NavigateAsync().ConfigureAwait(false);

        await Expect(_agentPage.Root).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.AgentSelect).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.AgentSelect).ToContainTextAsync("Daedalus Architect").ConfigureAwait(false);
        await Expect(_agentPage.NewSessionButton).ToBeEnabledAsync().ConfigureAwait(false);
        await Expect(_agentPage.ErrorAlert).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("New session → send 'hi' → streamed reply with tool card and usage → composer re-enabled → session listed")]
    public async Task AgentPage_NewSession_SendTurn_ShouldStreamReplyWithToolCard()
    {
        await _agentPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_agentPage.NewSessionButton).ToBeEnabledAsync().ConfigureAwait(false);

        // 1. New session → URL carries the session id and the chat column appears
        await _agentPage.CreateSessionAsync().ConfigureAwait(false);
        Page.Url.Should().Contain("/agent/");
        var sessionId = _agentPage.CurrentSessionId();
        sessionId.Should().NotBeNullOrWhiteSpace();
        await Expect(_agentPage.Composer).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.SessionTitle).ToContainTextAsync("Daedalus Architect").ConfigureAwait(false);

        // 2. Send a turn
        await _agentPage.SendAsync("hi").ConfigureAwait(false);
        await Expect(_agentPage.UserMessages).ToHaveCountAsync(1).ConfigureAwait(false);
        await Expect(_agentPage.UserMessages.First).ToContainTextAsync("hi").ConfigureAwait(false);

        // 3. Streamed reply: text, one succeeded roslyn__find_callers tool card, usage line
        await Expect(_agentPage.AssistantMessages).ToHaveCountAsync(1).ConfigureAwait(false);
        await Expect(_agentPage.AssistantTexts.First).ToContainTextAsync(StubAgentRuntime.ReplyText).ConfigureAwait(false);

        await Expect(_agentPage.ToolCards).ToHaveCountAsync(1).ConfigureAwait(false);
        var toolCard = _agentPage.ToolCard(StubAgentRuntime.ToolName);
        await Expect(toolCard).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(toolCard).ToHaveAttributeAsync("data-tool-status", "succeeded").ConfigureAwait(false);
        await Expect(toolCard).ToContainTextAsync(StubAgentRuntime.ToolName).ConfigureAwait(false);

        await Expect(_agentPage.Usage).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.Usage).ToContainTextAsync(StubAgentRuntime.ModelId).ConfigureAwait(false);
        await Expect(_agentPage.Usage).ToContainTextAsync("12 in / 7 out").ConfigureAwait(false);

        // 4. Turn finished: composer usable again, no error, no "Thinking…" indicator
        await Expect(_agentPage.SendButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.StopButton).Not.ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.Composer).ToBeEnabledAsync().ConfigureAwait(false);
        await Expect(_agentPage.StreamingIndicator).Not.ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.ErrorAlert).Not.ToBeVisibleAsync().ConfigureAwait(false);

        // 5. Session list shows the session with the recorded turn
        await Expect(_agentPage.SessionItems).Not.ToHaveCountAsync(0).ConfigureAwait(false);
        var activeItem = _agentPage.SessionItems.Filter(new LocatorFilterOptions { HasText = "1 turns" }).First;
        await Expect(activeItem).ToBeVisibleAsync().ConfigureAwait(false);

        // 6. Regression screenshot in the passing state (collected by the regression-test skill)
        await SaveRegressionScreenshotAsync("agent-page.png").ConfigureAwait(false);
    }

    [Test]
    [Description("Reopening a session by URL reloads the persisted transcript from the store")]
    public async Task AgentPage_ReopenSession_ShouldReloadTranscript()
    {
        await _agentPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_agentPage.NewSessionButton).ToBeEnabledAsync().ConfigureAwait(false);
        await _agentPage.CreateSessionAsync().ConfigureAwait(false);
        var sessionId = _agentPage.CurrentSessionId();
        sessionId.Should().NotBeNullOrWhiteSpace();

        await _agentPage.SendAsync("remember me").ConfigureAwait(false);
        await Expect(_agentPage.AssistantTexts.First).ToContainTextAsync(StubAgentRuntime.ReplyText).ConfigureAwait(false);
        await Expect(_agentPage.SendButton).ToBeVisibleAsync().ConfigureAwait(false);

        // Full reload: transcript must come back from the API (the store), not from page state.
        await _agentPage.NavigateToSessionAsync(sessionId!).ConfigureAwait(false);

        await Expect(_agentPage.UserMessages).ToHaveCountAsync(1).ConfigureAwait(false);
        await Expect(_agentPage.UserMessages.First).ToContainTextAsync("remember me").ConfigureAwait(false);
        await Expect(_agentPage.AssistantMessages).ToHaveCountAsync(1).ConfigureAwait(false);
        await Expect(_agentPage.AssistantTexts.First).ToContainTextAsync(StubAgentRuntime.ReplyText).ConfigureAwait(false);
        await Expect(_agentPage.SessionTitle).ToContainTextAsync(new Regex("Idle")).ConfigureAwait(false);
    }

    /// <summary>Writes the screenshot to <c>docs/regression-screenshots/</c> at the repository root (found by walking up to <c>Daedalus.sln</c>).</summary>
    private async Task SaveRegressionScreenshotAsync(string fileName)
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Daedalus.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            await TestContext.Progress.WriteLineAsync("Daedalus.sln not found above the test directory; regression screenshot skipped.").ConfigureAwait(false);
            return;
        }

        var screenshotsDir = Path.Combine(dir.FullName, "docs", "regression-screenshots");
        Directory.CreateDirectory(screenshotsDir);
        await Page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(screenshotsDir, fileName), FullPage = true
        }).ConfigureAwait(false);
    }
}
