namespace Daedalus.Tests.Playwright.Browser.PageObjects;

public sealed class ExecutionsPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator PageTitle =>
        _page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Task Executions" });

    public ILocator FilterCard => _page.Locator("[data-testid='filter-card']");
    public ILocator FilterDropDown => _page.Locator("[data-testid='filter-card'] .rz-dropdown");
    public ILocator FilterTextInput => _page.Locator("[data-testid='filter-card'] input.rz-textbox");
    public ILocator SearchButton => _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Search" });
    public ILocator DataGrid => _page.Locator(".rz-data-grid");
    public ILocator DataGridRows => _page.Locator(".rz-data-grid-data tbody tr");
    public ILocator ErrorAlert => _page.Locator(".rz-alert:has-text('error'), .rz-alert:has-text('Error')");
    public ILocator InfoAlert => _page.Locator(".rz-alert:has-text('Select a filter')");
    public async Task NavigateAsync() => await NavigateToAsync("/executions").ConfigureAwait(false);
}
