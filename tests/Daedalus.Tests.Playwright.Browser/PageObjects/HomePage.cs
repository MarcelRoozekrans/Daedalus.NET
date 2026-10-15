namespace Daedalus.Tests.Playwright.Browser.PageObjects;

/// <summary>Home for a signed-in user: what needs the person, in four cards, and one New task button.</summary>
public sealed class HomePage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    /// <summary>The awaiting approval card's test id.</summary>
    public const string Awaiting = "needs-me-awaiting";

    /// <summary>The running card's test id.</summary>
    public const string Running = "needs-me-running";

    /// <summary>The failed in the last 7 days card's test id.</summary>
    public const string Failed = "needs-me-failed";

    /// <summary>The recent pull requests card's test id.</summary>
    public const string PullRequests = "needs-me-prs";

    public ILocator Title => _page.Locator("[data-testid='home-title']");

    /// <summary>The signed-in Home's content: the header and the four cards.</summary>
    public ILocator Panel => _page.Locator("[data-testid='needs-me']");

    public ILocator NewTaskButton =>
        _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New task", Exact = true });

    /// <summary>The page's alert for a task that was created but whose manufacture was refused.</summary>
    public ILocator ManufactureRefusedAlert => _page.Locator("[data-testid='home-manufacture-refused']");

    public ILocator Card(string testId) => _page.Locator($"[data-testid='{testId}']");

    public ILocator CardHeading(string testId) => Card(testId).Locator("h6");

    /// <summary>The card's row that contains <paramref name="text"/>.</summary>
    public ILocator Row(string testId, string text) =>
        Card(testId).Locator("[data-testid='needs-me-row']").Filter(new LocatorFilterOptions { HasText = text });

    public ILocator Empty(string testId) => Card(testId).Locator("[data-testid='needs-me-empty']");

    public ILocator Error(string testId) => Card(testId).Locator("[data-testid='needs-me-error']");

    public async Task NavigateAsync() => await NavigateToAsync("/").ConfigureAwait(false);
}
