namespace Daedalus.Tests.Playwright.Browser.PageObjects;

/// <summary>Page object for <c>/agent</c> (Thalos chat page); selectors are the page's <c>data-testid</c>s.</summary>
public sealed class AgentPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator Root => _page.Locator("[data-testid='agent-page']");
    public ILocator AgentSelect => _page.Locator("[data-testid='agent-select']");
    public ILocator NewSessionButton => _page.Locator("[data-testid='agent-new-session']");
    public ILocator SessionItems => _page.Locator("[data-testid='agent-session-item']");
    public ILocator SessionTitle => _page.Locator("[data-testid='agent-session-title']");
    public ILocator Composer => _page.Locator("[data-testid='agent-composer']");
    public ILocator SendButton => _page.Locator("[data-testid='agent-send']");
    public ILocator StopButton => _page.Locator("[data-testid='agent-stop']");
    public ILocator Messages => _page.Locator("[data-testid='agent-messages']");
    public ILocator UserMessages => _page.Locator("[data-testid='agent-message-user']");
    public ILocator AssistantMessages => _page.Locator("[data-testid='agent-message-assistant']");
    public ILocator AssistantTexts => _page.Locator("[data-testid='agent-message-text']");
    public ILocator ToolCards => _page.Locator("[data-testid='agent-tool-card']");
    public ILocator Usage => _page.Locator("[data-testid='agent-usage']");
    public ILocator ErrorAlert => _page.Locator("[data-testid='agent-error']");
    public ILocator StreamingIndicator => _page.Locator("[data-testid='agent-streaming']");

    public ILocator MemoriesToggle => _page.Locator("[data-testid='agent-memories-toggle']");
    public ILocator MemoriesPanel => _page.Locator("[data-testid='agent-memories-panel']");
    public ILocator RecallStatus => _page.Locator("[data-testid='agent-recall-status']");
    public ILocator RecalledItems => _page.Locator("[data-testid='agent-recalled-item']");
    public ILocator MemoryItems => _page.Locator("[data-testid='agent-memory-item']");
    public ILocator MemoryKindFilter => _page.Locator("[data-testid='agent-memory-kind-filter']");
    public ILocator MemoriesEmpty => _page.Locator("[data-testid='agent-memories-empty']");

    public ILocator ToolCard(string toolName) => _page.Locator($"[data-testid='agent-tool-card'][data-tool-name='{toolName}']");

    /// <summary>The browse-list card whose text contains <paramref name="text"/>.</summary>
    public ILocator MemoryItem(string text) => _page.Locator("[data-testid='agent-memory-item']", new PageLocatorOptions { HasText = text });

    /// <summary>The forget button of the browse-list card whose text contains <paramref name="text"/>.</summary>
    public ILocator MemoryForgetButton(string text) => MemoryItem(text).Locator("[data-testid='agent-memory-forget']");

    /// <summary>Opens the memories panel and waits for it to render.</summary>
    public async Task OpenMemoriesAsync()
    {
        await MemoriesToggle.ClickAsync().ConfigureAwait(false);
        await MemoriesPanel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible }).ConfigureAwait(false);
    }

    public async Task NavigateAsync() => await NavigateToAsync("/agent").ConfigureAwait(false);

    public async Task NavigateToSessionAsync(string sessionId) => await NavigateToAsync($"/agent/{sessionId}").ConfigureAwait(false);

    /// <summary>Clicks "New session" and waits for the URL to carry the new session id.</summary>
    public async Task CreateSessionAsync()
    {
        await NewSessionButton.ClickAsync().ConfigureAwait(false);
        await _page.WaitForURLAsync(url => url.Contains("/agent/", StringComparison.Ordinal)).ConfigureAwait(false);
    }

    /// <summary>The session id segment of the current URL, or <see langword="null"/> when no session is open.</summary>
    public string? CurrentSessionId()
    {
        var path = new Uri(_page.Url).AbsolutePath;
        const string prefix = "/agent/";
        return path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length ? path[prefix.Length..] : null;
    }

    public async Task SendAsync(string text)
    {
        await Composer.FillAsync(text).ConfigureAwait(false);
        await SendButton.ClickAsync().ConfigureAwait(false);
    }
}
