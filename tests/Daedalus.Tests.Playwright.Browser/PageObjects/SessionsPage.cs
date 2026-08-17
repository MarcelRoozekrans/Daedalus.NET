namespace Daedalus.Tests.Playwright.Browser.PageObjects;

public sealed class SessionsPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator PageTitle =>
        _page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Sessions", Exact = true });

    public ILocator AllSessionsButton => _page.Locator("[data-testid='btn-all-sessions']");
    public ILocator ActiveOnlyButton => _page.Locator("[data-testid='btn-active-sessions']");
    public ILocator DataGrid => _page.Locator("[data-testid='sessions-grid']");
    public ILocator DataGridRows => _page.Locator("[data-testid='sessions-grid'] .rz-data-grid-data tbody tr");
    public ILocator ErrorAlert => _page.Locator(".rz-alert");
    public async Task NavigateAsync() => await NavigateToAsync("/sessions").ConfigureAwait(false);
}
