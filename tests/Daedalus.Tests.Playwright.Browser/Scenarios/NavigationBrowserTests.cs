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
        if (!SetUpCompleted)
            return;
        _mainPage = new MainPage(Page, BaseUrl);
    }

    [Test]
    [Description("Layout should render header with app title")]
    public async Task Layout_ShouldRender_HeaderWithTitle()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await Expect(_mainPage.Header).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.AppTitle).ToBeVisibleAsync().ConfigureAwait(false);
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
    [Description("Clicking Git Repositories menu item should navigate to /repositories")]
    public async Task Sidebar_ClickGitRepositories_ShouldNavigate()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await _mainPage.NavigateToMenuItemAsync("Git Repositories").ConfigureAwait(false);
        await Page.WaitForURLAsync("**/repositories").ConfigureAwait(false);
        Page.Url.Should().Contain("/repositories");
    }

    [Test]
    [Description("Clicking PRD Generator menu item should navigate to /prd-generator")]
    public async Task Sidebar_ClickPrdGenerator_ShouldNavigate()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await _mainPage.NavigateToMenuItemAsync("PRD Generator").ConfigureAwait(false);
        await Page.WaitForURLAsync("**/prd-generator").ConfigureAwait(false);
        Page.Url.Should().Contain("/prd-generator");
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

    // ── Bug #2 regression: sidebar section dividers ──────────────────────────

    [Test]
    [Description("Bug #2 regression: sidebar section dividers should be visible")]
    public async Task Sidebar_SectionDividers_ShouldBeVisible()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await Expect(_mainPage.OperationsDivider).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.ConfigurationDivider).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.AiToolsDivider).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Bug #2 regression: all sidebar menu items should be clickable and navigate to correct routes")]
    public async Task Sidebar_AllMenuItems_ShouldNavigateCorrectly()
    {
        var menuRoutes = new (string MenuText, string ExpectedPath)[]
        {
            ("Tasks", "/tasks"),
            ("Projects", "/projects"),
            ("Sessions", "/sessions"),
            ("Executions", "/executions"),
            ("Ralph Config", "/ralph-config"),
            ("Git Repositories", "/repositories"),
            ("PRD Generator", "/prd-generator"),
            ("Dashboard", "/")
        };

        foreach (var (menuText, expectedPath) in menuRoutes)
        {
            await NavigateToAsync("/").ConfigureAwait(false);
            await _mainPage.NavigateToMenuItemAsync(menuText).ConfigureAwait(false);
            await Page.WaitForURLAsync($"**{expectedPath}").ConfigureAwait(false);
            Page.Url.Should().Contain(expectedPath, $"clicking '{menuText}' should navigate to '{expectedPath}'");
        }
    }

    // ── Bug #3 regression: no empty error messages ───────────────────────────

    [Test]
    [Description("Bug #3 regression: error messages should never display empty 'Unexpected error: '")]
    public async Task ErrorMessages_ShouldNeverBeEmpty()
    {
        var pages = new[] { "/tasks", "/projects", "/sessions", "/ralph-config" };

        foreach (var pagePath in pages)
        {
            await NavigateToAsync(pagePath).ConfigureAwait(false);
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

            var alerts = Page.Locator(".rz-alert");
            var count = await alerts.CountAsync().ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                var alertText = await alerts.Nth(i).TextContentAsync().ConfigureAwait(false) ?? "";
                alertText.Should().NotContain("Unexpected error: ''",
                    $"page '{pagePath}' should not display empty error messages");
                alertText.Should().NotEndWith("Unexpected error: ",
                    $"page '{pagePath}' should not display truncated error messages");
            }
        }
    }

    // ── Bug #4 regression: login callback route ──────────────────────────────

    [Test]
    [Description("Bug #4 regression: application should serve the login callback route")]
    public async Task LoginCallback_Route_ShouldExist()
    {
        var response = await Page.APIRequest.GetAsync(
            new Uri(BaseUrl, "/authentication/login-callback").ToString()).ConfigureAwait(false);

        response.Status.Should().NotBe(404,
            "the /authentication/login-callback route should exist for OIDC redirect handling");
    }

    // ── Layout structure ─────────────────────────────────────────────────────

    [Test]
    [Description("Footer should display version and platform info")]
    public async Task Footer_ShouldDisplay_VersionInfo()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await Expect(_mainPage.Footer).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.Footer).ToContainTextAsync("Daedalus").ConfigureAwait(false);
        await Expect(_mainPage.Footer).ToContainTextAsync("v1.0.0").ConfigureAwait(false);
    }
}
