namespace Daedalus.Tests.Playwright.Browser.PageObjects;

public sealed class HomePage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator Title => _page.Locator("[data-testid='home-title']");
    public ILocator StatsRow => _page.Locator("[data-testid='stats-row']");
    public ILocator StatCards => _page.Locator("[data-testid^='stat-']");
    public ILocator TotalTasksStat => _page.Locator("[data-testid='stat-total-tasks']");
    public ILocator ProjectsStat => _page.Locator("[data-testid='stat-projects']");
    public ILocator QuickActionsCard => _page.Locator("[data-testid='quick-actions']");
    public ILocator QuickActionButtons => _page.Locator("[data-testid='quick-actions'] .rz-button");
    public ILocator CreateTaskButton => _page.GetByText("Create New Task");
    public ILocator ViewCostsButton => _page.GetByText("View Costs");
    public ILocator GeneratePrdButton => _page.GetByText("Generate PRD");
    public ILocator ManageProjectsButton => _page.GetByText("Manage Projects");
    public ILocator ErrorAlert => _page.Locator(".rz-alert");
    public async Task NavigateAsync() => await NavigateToAsync("/").ConfigureAwait(false);
}
