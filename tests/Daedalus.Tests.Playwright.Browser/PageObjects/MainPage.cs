namespace Daedalus.Tests.Playwright.Browser.PageObjects;

public sealed class MainPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator Header => _page.Locator(".rz-header");
    public ILocator AppTitle => _page.Locator(".rz-header").GetByText("Daedalus", new LocatorGetByTextOptions { Exact = true });
    public ILocator RalphBadge => _page.Locator(".rz-header .rz-badge", new PageLocatorOptions { HasText = "Ralph Loop" });
    public ILocator SidebarToggle => _page.Locator(".rz-sidebar-toggle");
    public ILocator Sidebar => _page.Locator(".rz-sidebar");
    public ILocator PanelMenu => _page.Locator(".rz-panel-menu");
    public ILocator DashboardMenuItem => _page.GetByText("Dashboard", new PageGetByTextOptions { Exact = true });
    public ILocator TasksMenuItem => _page.GetByText("Tasks", new PageGetByTextOptions { Exact = true });
    public ILocator ProjectsMenuItem => _page.GetByText("Projects", new PageGetByTextOptions { Exact = true });
    public ILocator SessionsMenuItem => _page.GetByText("Sessions", new PageGetByTextOptions { Exact = true });
    public ILocator ExecutionsMenuItem => _page.GetByText("Executions", new PageGetByTextOptions { Exact = true });
    public ILocator RalphConfigMenuItem => _page.GetByText("Ralph Config", new PageGetByTextOptions { Exact = true });
    public ILocator PrdGeneratorMenuItem => _page.GetByText("PRD Generator", new PageGetByTextOptions { Exact = true });

    public ILocator GitRepositoriesMenuItem =>
        _page.GetByText("Git Repositories", new PageGetByTextOptions { Exact = true });

    public ILocator AllMenuItems => _page.Locator(".rz-panel-menu .rz-navigation-item");

    public async Task NavigateToMenuItemAsync(string itemText) =>
        await _page.GetByText(itemText, new PageGetByTextOptions { Exact = true }).ClickAsync().ConfigureAwait(false);
}
