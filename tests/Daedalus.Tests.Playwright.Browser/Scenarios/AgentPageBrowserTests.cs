using System.Text.RegularExpressions;
using AwesomeAssertions.Execution;
using Daedalus.Tests.Playwright.Browser.Fixtures;
using Daedalus.Tests.Playwright.Browser.PageObjects;
using Daedalus.Tests.Playwright.Browser.Utilities;

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
        if (!SetUpCompleted)
            return;
        _agentPage = new AgentPage(Page, BaseUrl);
    }

    /// <summary>
    ///     On a phone, at 375px, the page is one column: the sessions above the chat, each the column's full width, and
    ///     the empty state's sentence reads across it in at most two lines, not a word a line.
    ///     Red: the phone rule's <c>flex-direction: column</c> removed; the empty state starts 31px above the sessions'
    ///     bottom, beside them. The columns back in a horizontal <c>RadzenStack</c>, as before, fail the same way: its
    ///     inline row direction beats the phone rule.
    ///     Red: <c>width: 100%</c> dropped from the phone rule for the sessions; they stay 280px in a 295px column.
    ///     Red: the chat column held to the 70px the review measured; the sentence takes 192px, eight lines.
    /// </summary>
    [Test]
    [Description("The Agent page is one column on a phone")]
    public async Task The_agent_page_is_one_column_on_a_phone()
    {
        await Page.SetViewportSizeAsync(375, 812).ConfigureAwait(false);
        await _agentPage.NavigateAsync().ConfigureAwait(false);
        var sessions = Page.Locator("[data-testid='agent-sessions']");
        var empty = Page.Locator("[data-testid='agent-empty']");
        await Expect(empty).ToBeVisibleAsync().ConfigureAwait(false);

        var pageBox = (await _agentPage.Root.BoundingBoxAsync().ConfigureAwait(false))!;
        var sessionsBox = (await sessions.BoundingBoxAsync().ConfigureAwait(false))!;
        var emptyBox = (await empty.BoundingBoxAsync().ConfigureAwait(false))!;
        var lineHeight = await empty.EvaluateAsync<float>("e => parseFloat(getComputedStyle(e).lineHeight)").ConfigureAwait(false);

        emptyBox.Y.Should().BeGreaterThanOrEqualTo(sessionsBox.Y + sessionsBox.Height - 1, "the chat is under the sessions");
        sessionsBox.Width.Should().BeApproximately(pageBox.Width, 1, "the sessions take the column's width");
        emptyBox.Height.Should().BeLessThanOrEqualTo((lineHeight * 2) + 1, "the empty state reads across the column");
    }

    /// <summary>
    ///     With a session open the page is exactly as tall as the content column, at desktop, tablet and phone widths:
    ///     the composer and Send sit above the footer, the body has nothing to scroll, and the page reaches the column's
    ///     bottom, so the composer sits at its foot.
    ///     Red: <c>.agent-page</c> back on <c>height: calc(100vh - 80px)</c> without <c>page-fill</c>; the composer's and Send's
    ///     bottoms are 38px under the footer's top, the body scrolls 78px and the page ends 78px past the column, at
    ///     1920x1080 and 768x1024; 52px and 92px at 375x812. All four assertions go red.
    ///     Red: the theme's <c>.rz-body > div:has(> .page-fill)</c> rule removed; the wrapper is only as tall as its
    ///     content, the page's 100% resolves to auto, and the page ends hundreds of pixels above the column's bottom.
    ///     Only the gap assertion goes red: a short transcript still fits.
    /// </summary>
    [TestCase(1920, 1080)]
    [TestCase(768, 1024)]
    [TestCase(375, 812)]
    [Description("With a session open the Agent page fits above the footer")]
    public async Task With_a_session_open_the_agent_page_fits_above_the_footer(int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height).ConfigureAwait(false);
        await _agentPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_agentPage.NewSessionButton).ToBeEnabledAsync().ConfigureAwait(false);
        await _agentPage.CreateSessionAsync().ConfigureAwait(false);
        await Expect(_agentPage.Composer).ToBeVisibleAsync().ConfigureAwait(false);

        var composerOverlap = await LayoutFit.OverlapWithFooterAsync(Page, _agentPage.Composer).ConfigureAwait(false);
        var sendOverlap = await LayoutFit.OverlapWithFooterAsync(Page, _agentPage.SendButton).ConfigureAwait(false);
        var bodyOverflow = await LayoutFit.BodyOverflowAsync(Page).ConfigureAwait(false);
        var gapBelow = await LayoutFit.GapBelowAsync(Page, _agentPage.Root).ConfigureAwait(false);

        // All four are reported together: each Red line turns its own assertion red.
        using (new AssertionScope())
        {
            composerOverlap.Should().BeLessThanOrEqualTo(0, "the composer's bottom is above the footer's top");
            sendOverlap.Should().BeLessThanOrEqualTo(0, "Send's bottom is above the footer's top");
            bodyOverflow.Should().BeLessThanOrEqualTo(0, "the page fits the column, so the body does not scroll");
            gapBelow.Should().BeInRange(-1, 1, "the page fills the column down to its bottom, so the composer sits at the foot");
        }
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
    [Description("Turn recalls the visible memories → panel lists them, hides another agent's; forgetting one persists")]
    public async Task AgentPage_Memories_ShouldShowRecalled_AndForget()
    {
        await _agentPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_agentPage.NewSessionButton).ToBeEnabledAsync().ConfigureAwait(false);
        await _agentPage.CreateSessionAsync().ConfigureAwait(false);
        var sessionId = _agentPage.CurrentSessionId();
        sessionId.Should().NotBeNullOrWhiteSpace();

        await _agentPage.SendAsync("hi").ConfigureAwait(false);
        await Expect(_agentPage.AssistantTexts.Last).ToContainTextAsync(StubAgentRuntime.ReplyText).ConfigureAwait(false);
        await Expect(_agentPage.SendButton).ToBeVisibleAsync().ConfigureAwait(false);

        // 1. "Recalled this turn": the memory-recalled event's ids, hydrated through GET /api/agent-memories/{id}
        await _agentPage.OpenMemoriesAsync().ConfigureAwait(false);
        await Expect(_agentPage.MemoriesPanel).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.RecallStatus).ToContainTextAsync("recalled").ConfigureAwait(false);
        await Expect(_agentPage.RecalledItems).ToHaveCountAsync(3).ConfigureAwait(false);
        await Expect(_agentPage.MemoriesPanel).ToContainTextAsync("prefers xUnit").ConfigureAwait(false);

        // 2. Browse list: own + shared (marked "shared"); the memory pinned to another agent is invisible to both lists
        await Expect(_agentPage.MemoryItems).ToHaveCountAsync(3).ConfigureAwait(false);
        await Expect(_agentPage.MemoryItem("prefers xUnit")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.MemoryItem("data-testid")).ToContainTextAsync("shared").ConfigureAwait(false);
        await Expect(_agentPage.MemoriesEmpty).Not.ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_agentPage.MemoriesPanel).Not.ToContainTextAsync(E2EServerFixture.PinnedMemoryText).ConfigureAwait(false);

        await SaveRegressionScreenshotAsync("agent-memories.png").ConfigureAwait(false);

        // 3. Forget archives this scenario's own disposable memory; the card goes away without a reload
        await _agentPage.MemoryForgetButton(E2EServerFixture.DisposableMemoryText).ClickAsync().ConfigureAwait(false);
        await Expect(_agentPage.MemoryItem(E2EServerFixture.DisposableMemoryText)).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(_agentPage.MemoryItems).ToHaveCountAsync(2).ConfigureAwait(false);

        // 4. …and it stays forgotten: a full reload re-reads the list from the API
        await _agentPage.NavigateToSessionAsync(sessionId!).ConfigureAwait(false);
        await _agentPage.OpenMemoriesAsync().ConfigureAwait(false);
        await Expect(_agentPage.MemoryItems).ToHaveCountAsync(2).ConfigureAwait(false);
        await Expect(_agentPage.MemoriesPanel).Not.ToContainTextAsync(E2EServerFixture.DisposableMemoryText).ConfigureAwait(false);
        await Expect(_agentPage.MemoryItem("prefers xUnit")).ToBeVisibleAsync().ConfigureAwait(false);
        // The reload also proves "recalled this turn" does not survive a session load.
        await Expect(_agentPage.RecalledItems).ToHaveCountAsync(0).ConfigureAwait(false);
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
}
