namespace Daedalus.Tests.Playwright.Browser.PageObjects;

public sealed class MainPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator Header => _page.Locator(".rz-header");
    public ILocator AppTitle => _page.Locator(".rz-header").GetByText("Daedalus", new LocatorGetByTextOptions { Exact = true });
    public ILocator SidebarToggle => _page.Locator(".rz-sidebar-toggle");
    public ILocator Sidebar => _page.Locator(".rz-sidebar");
    public ILocator PanelMenu => _page.Locator(".rz-panel-menu");
    public ILocator Footer => _page.Locator(".rz-footer");

    // Sidebar section dividers
    public ILocator OperationsDivider => _page.Locator(".sidebar-section-divider", new PageLocatorOptions { HasText = "Operations" });
    public ILocator AdminDivider => _page.Locator(".sidebar-section-divider", new PageLocatorOptions { HasText = "Admin" });

    // Menu items
    public ILocator HomeMenuItem => _page.Locator(".rz-sidebar").GetByText("Home", new LocatorGetByTextOptions { Exact = true });
    public ILocator TasksMenuItem => _page.Locator(".rz-sidebar").GetByText("Tasks", new LocatorGetByTextOptions { Exact = true });
    public ILocator ProjectsMenuItem => _page.Locator(".rz-sidebar").GetByText("Projects", new LocatorGetByTextOptions { Exact = true });
    public ILocator AgentMenuItem => _page.Locator(".rz-sidebar").GetByText("Agent", new LocatorGetByTextOptions { Exact = true });
    public ILocator PrdGeneratorMenuItem => _page.Locator(".rz-sidebar").GetByText("PRD Generator", new LocatorGetByTextOptions { Exact = true });
    public ILocator GitRepositoriesMenuItem => _page.Locator(".rz-sidebar").GetByText("Git Repositories", new LocatorGetByTextOptions { Exact = true });

    public ILocator AllMenuItems => _page.Locator(".rz-panel-menu .rz-navigation-item");

    public async Task NavigateToMenuItemAsync(string itemText) =>
        await _page.Locator(".rz-sidebar").GetByText(itemText, new LocatorGetByTextOptions { Exact = true }).ClickAsync().ConfigureAwait(false);
}
