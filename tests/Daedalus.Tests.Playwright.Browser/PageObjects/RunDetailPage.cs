namespace Daedalus.Tests.Playwright.Browser.PageObjects;

/// <summary>One run at <c>/runs/{id}</c>: its header, gate, tabs and the cancel dialog.</summary>
public sealed class RunDetailPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator Header => ById("run-header");
    public ILocator Status => Header.Locator("[data-testid='run-status']");
    public ILocator Retry => ById("run-retry");
    public ILocator LastError => ById("run-last-error");
    public ILocator NotFound => ById("run-not-found");
    public ILocator Forbidden => ById("run-forbidden");
    public ILocator Error => ById("run-error");
    public ILocator Timeline => ById("run-timeline");
    public ILocator Steps => ById("run-step");
    public ILocator GatePanel => ById("gate-panel");
    public ILocator GateFindings => _page.Locator("[data-testid^='gate-drop-']");
    public ILocator Approve => ById("gate-approve");
    public ILocator Cancel => ById("gate-cancel");
    public ILocator CancelReason => _page.Locator("[data-testid='cancel-reason']");
    public ILocator CancelKeep => ById("cancel-keep");
    public ILocator CancelConfirm => ById("cancel-confirm");
    public ILocator Dialog => _page.Locator(".rz-dialog");
    public ILocator TabHeaders => _page.Locator(".run-tabs .rz-tabview-nav li");
    public ILocator Notification => _page.Locator(".rz-notification");

    public async Task NavigateAsync(Guid id) => await NavigateToAsync($"/runs/{id}").ConfigureAwait(false);

    /// <summary>The checkbox that drops deferred finding <paramref name="findingId"/>.</summary>
    public ILocator DropFinding(string findingId) => ById($"gate-drop-{findingId}");

    /// <summary>The tab header that reads <paramref name="name"/>.</summary>
    public ILocator Tab(string name) => TabHeaders.Filter(new LocatorFilterOptions { HasText = name });

    private ILocator ById(string testId) => _page.Locator($"[data-testid='{testId}']");
}
