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

    public ILocator ToolCard(string toolName) => _page.Locator($"[data-testid='agent-tool-card'][data-tool-name='{toolName}']");

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
