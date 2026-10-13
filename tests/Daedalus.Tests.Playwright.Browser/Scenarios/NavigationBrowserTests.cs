using Daedalus.Tests.Playwright.Browser.PageObjects;
using Daedalus.Tests.Playwright.Browser.Utilities;

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
    [Description("Sidebar should have all six navigation menu items")]
    public async Task Sidebar_ShouldDisplay_AllMenuItems()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await Expect(_mainPage.Sidebar).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.DashboardMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.TasksMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.ProjectsMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.ExecutionsMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
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
    [Description("Clicking Executions menu item should navigate to /executions")]
    public async Task Sidebar_ClickExecutions_ShouldNavigate()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await _mainPage.NavigateToMenuItemAsync("Executions").ConfigureAwait(false);
        await Page.WaitForURLAsync("**/executions").ConfigureAwait(false);
        Page.Url.Should().Contain("/executions");
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

    /// <summary>
    ///     Git Repositories and its Configuration heading are for admins only, as the repositories API is, and follow a
    ///     role change without a reload: absent for an analyst, present once an admin, absent again as a developer.
    ///     Red: <c>MainLayout</c> shows the item to everyone; the analyst sees it.
    ///     Red: the item rendered for admins, but its Configuration heading outside the <c>AuthorizeView</c>; the analyst
    ///     sees the heading.
    /// </summary>
    [Test]
    [Description("Git Repositories is shown to admins only, and follows a role change")]
    public async Task Sidebar_GitRepositories_IsForAdminsOnly()
    {
        await using var context = await Browser.NewContextAsync().ConfigureAwait(false);
        await TestRoles.WriteAsync(context, BaseUrl, "analyst").ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var main = new MainPage(page, BaseUrl);
        await main.NavigateToAsync("/").ConfigureAwait(false);

        // The header names the user only once the user is known, so the absence is checked after it.
        await Expect(main.Header).ToContainTextAsync("E2E Test User").ConfigureAwait(false);
        await Expect(main.GitRepositoriesMenuItem).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(main.ConfigurationDivider).ToHaveCountAsync(0).ConfigureAwait(false);

        await TestRoles.WriteAsync(context, BaseUrl, "admin").ConfigureAwait(false);
        await Expect(main.GitRepositoriesMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(main.ConfigurationDivider).ToBeVisibleAsync().ConfigureAwait(false);

        await TestRoles.WriteAsync(context, BaseUrl, "developer").ConfigureAwait(false);
        await Expect(main.GitRepositoriesMenuItem).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(main.ConfigurationDivider).ToHaveCountAsync(0).ConfigureAwait(false);
    }

    /// <summary>
    ///     The content column is as wide, and the page starts at the same x, on every page, at desktop and tablet widths:
    ///     a short page does not shrink the layout to its content, and a page long enough to scroll keeps the width a
    ///     short one has. The body never scrolls sideways. <c>clientWidth</c> is measured, the width the content gets,
    ///     which a scrollbar takes from. A page's start is its first heading, or its first element when it has none.
    ///     Red: <c>#app</c> centres its child again, as the loading screen's style did; the Dashboard's body is narrower
    ///     than the Tasks one's.
    ///     Red: no <c>scrollbar-gutter</c> on the body; a page long enough to scroll has a narrower body.
    /// </summary>
    [Test]
    [Description("The content column has the same width and start on every page")]
    public async Task Layout_ContentColumn_IsTheSameOnEveryPage()
    {
        string[] paths =
            ["/", "/tasks", "/runs", "/projects", "/executions", "/schedules", "/costs", "/agent", "/prd-generator", "/repositories"];

        // Headless Chromium hides scrollbars unless told not to, and a hidden scrollbar takes no width, so this test
        // uses a browser of its own that shows them, as a person's browser does.
        await using var browser = await BrowserType.LaunchAsync(new BrowserTypeLaunchOptions
        {
            IgnoreDefaultArgs = ["--hide-scrollbars"],
        }).ConfigureAwait(false);
        var page = await browser.NewPageAsync().ConfigureAwait(false);
        foreach (var (width, height) in new[] { (1280, 800), (1024, 800) })
        {
            await page.SetViewportSizeAsync(width, height).ConfigureAwait(false);
            var measured = new List<ContentColumn>();
            foreach (var path in paths)
            {
                // Network idle, so a page's data, and with it its length, has arrived before it is measured.
                await page.GotoAsync(new Uri(BaseUrl, path).ToString()).ConfigureAwait(false);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);
                await Expect(page.Locator(PageStartSelector).First).ToBeVisibleAsync().ConfigureAwait(false);
                var json = await page.EvaluateAsync<string>(ContentColumnScript).ConfigureAwait(false);
                measured.Add(JsonSerializer.Deserialize<ContentColumn>(json)! with { Path = path });
            }

            var report = string.Join("; ", measured.Select(m => $"{m.Path} {m.Width}w {m.StartX}x {m.Overflow}o {m.Scrolls}s"));
            await TestContext.Progress.WriteLineAsync($"{width}x{height}: {report}").ConfigureAwait(false);
            measured.Should().OnlyContain(m => Math.Abs(m.Width - measured[0].Width) <= 1,
                $"the body is as wide on every page at {width}x{height}: {report}");
            measured.Should().OnlyContain(m => Math.Abs(m.StartX - measured[0].StartX) <= 1,
                $"every page starts at the same x at {width}x{height}: {report}");
            measured.Should().OnlyContain(m => m.Overflow <= 0, $"the body never scrolls sideways at {width}x{height}: {report}");
        }
    }

    /// <summary>A page's first heading, or the Agent page, which has none.</summary>
    private const string PageStartSelector = ".rz-body :is(h1, h2, h3, h4, h5, h6), [data-testid='agent-page']";

    private const string ContentColumnScript = """
        () => {
            const body = document.querySelector('.rz-body');
            const start = body.querySelector(':is(h1, h2, h3, h4, h5, h6)') ?? body.firstElementChild.firstElementChild;
            return JSON.stringify({
                Width: body.clientWidth,
                StartX: start.getBoundingClientRect().x,
                Overflow: body.scrollWidth - body.clientWidth,
                Scrolls: body.scrollHeight > body.clientHeight,
            });
        }
        """;

    private sealed record ContentColumn(double Width, double StartX, double Overflow, bool Scrolls)
    {
        public string Path { get; init; } = "";
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
            ("Executions", "/executions"),
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
        var pages = new[] { "/tasks", "/projects" };

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
