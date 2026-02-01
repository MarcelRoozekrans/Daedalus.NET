using Daedalus.Tests.Playwright.Browser.PageObjects;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Navigation")]
[Description("Navigation browser tests validating Radzen sidebar menu")]
public class NavigationBrowserTests : BrowserTestBase
{
    private MainPage _mainPage = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted) return;
        _mainPage = new MainPage(Page, BaseUrl);
    }

    [Test]
    [Description("Layout should render header with app title and badge")]
    public async Task Layout_ShouldRender_HeaderWithTitleAndBadge()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await Expect(_mainPage.Header).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.AppTitle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.RalphBadge).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Sidebar should have all eight navigation menu items")]
    public async Task Sidebar_ShouldDisplay_AllMenuItems()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await Expect(_mainPage.Sidebar).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.DashboardMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.TasksMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.ProjectsMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.SessionsMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.ExecutionsMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.RalphConfigMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.PrdGeneratorMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.GitRepositoriesMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Clicking Tasks menu item should navigate to /tasks")]
    public async Task Sidebar_ClickTasks_ShouldNavigate()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await _mainPage.NavigateToMenuItemAsync("Tasks").ConfigureAwait(false);
        await Page.WaitForURLAsync("**/tasks").ConfigureAwait(false);
        Page.Url.Should().Contain("/tasks");
    }

    [Test]
    [Description("Clicking Projects menu item should navigate to /projects")]
    public async Task Sidebar_ClickProjects_ShouldNavigate()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await _mainPage.NavigateToMenuItemAsync("Projects").ConfigureAwait(false);
        await Page.WaitForURLAsync("**/projects").ConfigureAwait(false);
        Page.Url.Should().Contain("/projects");
    }

    [Test]
    [Description("Clicking Sessions menu item should navigate to /sessions")]
    public async Task Sidebar_ClickSessions_ShouldNavigate()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await _mainPage.NavigateToMenuItemAsync("Sessions").ConfigureAwait(false);
        await Page.WaitForURLAsync("**/sessions").ConfigureAwait(false);
        Page.Url.Should().Contain("/sessions");
    }

    [Test]
    [Description("Clicking Executions menu item should navigate to /executions")]
    public async Task Sidebar_ClickExecutions_ShouldNavigate()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await _mainPage.NavigateToMenuItemAsync("Executions").ConfigureAwait(false);
        await Page.WaitForURLAsync("**/executions").ConfigureAwait(false);
        Page.Url.Should().Contain("/executions");
    }

    [Test]
    [Description("Clicking Ralph Config menu item should navigate to /ralph-config")]
    public async Task Sidebar_ClickRalphConfig_ShouldNavigate()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await _mainPage.NavigateToMenuItemAsync("Ralph Config").ConfigureAwait(false);
        await Page.WaitForURLAsync("**/ralph-config").ConfigureAwait(false);
        Page.Url.Should().Contain("/ralph-config");
    }

    [Test]
    [Description("Sidebar toggle should collapse/expand sidebar")]
    public async Task SidebarToggle_ShouldCollapseAndExpand()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await Expect(_mainPage.Sidebar).ToBeVisibleAsync().ConfigureAwait(false);
        await _mainPage.SidebarToggle.ClickAsync().ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false);
        await _mainPage.SidebarToggle.ClickAsync().ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false);
        await Expect(_mainPage.DashboardMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
    }
}
