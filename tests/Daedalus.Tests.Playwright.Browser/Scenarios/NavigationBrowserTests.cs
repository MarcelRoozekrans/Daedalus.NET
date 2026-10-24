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
    [Description("Sidebar should have the navigation menu items")]
    public async Task Sidebar_ShouldDisplay_AllMenuItems()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await Expect(_mainPage.Sidebar).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.HomeMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.TasksMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.ProjectsMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.AgentMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
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
        await Expect(_mainPage.HomeMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     The sidebar toggle can be seen against the header: its icon's contrast with the header background is at least
    ///     3:1, the WCAG minimum for a user-interface component. Below 1280px the sidebar starts collapsed, as
    ///     <c>ShellBrowserTests.On_a_tablet_the_sidebar_starts_collapsed</c> checks, so this button is the only way to the
    ///     menu there.
    ///     Red: drop <c>--rz-sidebar-toggle-color</c> from daedalus-theme.css; Material draws the icon white on the white
    ///     header, a contrast of 1:1.
    /// </summary>
    [Test]
    [Description("The sidebar toggle is visible against the header")]
    public async Task SidebarToggle_IsVisibleAgainstTheHeader()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await Expect(_mainPage.SidebarToggle).ToBeVisibleAsync().ConfigureAwait(false);

        var contrast = await Page.EvaluateAsync<double>(ContrastScript).ConfigureAwait(false);

        contrast.Should().BeGreaterThanOrEqualTo(3.0, "the toggle must be visible on the header");
    }

    private const string ContrastScript = """
        () => {
            const rgb = c => c.match(/\d+(\.\d+)?/g).slice(0, 3).map(Number);
            const lum = ([r, g, b]) => {
                const ch = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
                return 0.2126 * ch(r) + 0.7152 * ch(g) + 0.0722 * ch(b);
            };
            const icon = document.querySelector('.rz-sidebar-toggle i, .rz-sidebar-toggle .rzi');
            const header = document.querySelector('.rz-header');
            const a = lum(rgb(getComputedStyle(icon).color));
            const b = lum(rgb(getComputedStyle(header).backgroundColor));
            return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
        }
        """;

    /// <summary>
    ///     Git Repositories and its Admin heading are for admins only, as the repositories API is, and follow a
    ///     role change without a reload: absent for an analyst, present once an admin, absent again as a developer.
    ///     Red: <c>MainLayout</c> shows the item to everyone; the analyst sees it.
    ///     Red: the item rendered for admins, but its Admin heading outside the <c>AuthorizeView</c>; the analyst
    ///     sees the heading.
    /// </summary>
    [Test]
    [Description("Git Repositories is shown to admins only, and follows a role change")]
    public async Task Sidebar_GitRepositories_IsForAdminsOnly()
    {
        var context = await NewTracedContextAsync().ConfigureAwait(false);
        await TestRoles.WriteAsync(context, BaseUrl, "analyst").ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var main = new MainPage(page, BaseUrl);
        await main.NavigateToAsync("/").ConfigureAwait(false);

        // The header names the user only once the user is known, so the absence is checked after it.
        await Expect(main.Header).ToContainTextAsync("E2E Test User").ConfigureAwait(false);
        await Expect(main.GitRepositoriesMenuItem).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(main.AdminDivider).ToHaveCountAsync(0).ConfigureAwait(false);

        await TestRoles.WriteAsync(context, BaseUrl, "admin").ConfigureAwait(false);
        await Expect(main.GitRepositoriesMenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(main.AdminDivider).ToBeVisibleAsync().ConfigureAwait(false);

        await TestRoles.WriteAsync(context, BaseUrl, "developer").ConfigureAwait(false);
        await Expect(main.GitRepositoriesMenuItem).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(main.AdminDivider).ToHaveCountAsync(0).ConfigureAwait(false);
    }

    /// <summary>
    ///     The content column is as wide, and the page starts at the same x, on every page, at desktop and tablet widths:
    ///     a short page does not shrink the layout to its content, and a page long enough to scroll keeps the width a
    ///     short one has. The body never scrolls sideways. <c>clientWidth</c> is measured, the width the content gets,
    ///     which a scrollbar takes from. A page's start is its first heading, or its first element when it has none.
    ///     Red: <c>#app</c> centres its child again, as the loading screen's style did; the Home page's body is narrower
    ///     than the Tasks one's.
    ///     Red: no <c>scrollbar-gutter</c> on the body; a page long enough to scroll has a narrower body.
    ///     Red: only the pages too short to scroll measured, /costs and /agent left out; no measured page scrolls.
    ///     Red: <c>.rz-body &gt; div { min-width: 2000px; }</c> in the theme; every page scrolls sideways.
    /// </summary>
    [Test]
    [Description("The content column has the same width and start on every page")]
    public async Task Layout_ContentColumn_IsTheSameOnEveryPage()
    {
        string[] paths =
            ["/", "/tasks", "/runs", "/projects", "/schedules", "/costs", "/agent", "/prd-generator", "/repositories"];

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

            // The widths compare a page that scrolls with one that does not, or the gutter goes unchecked.
            measured.Should().Contain(m => m.Scrolls, $"a page long enough to scroll is measured at {width}x{height}: {report}");
            measured.Should().Contain(m => !m.Scrolls, $"a page too short to scroll is measured at {width}x{height}: {report}");
            measured.Should().OnlyContain(m => Math.Abs(m.Width - measured[0].Width) <= 1,
                $"the body is as wide on every page at {width}x{height}: {report}");
            measured.Should().OnlyContain(m => Math.Abs(m.StartX - measured[0].StartX) <= 1,
                $"every page starts at the same x at {width}x{height}: {report}");
            measured.Should().OnlyContain(m => m.Overflow <= 0, $"the body never scrolls sideways at {width}x{height}: {report}");
        }
    }

    /// <summary>A page's first heading: every page has the shared page header.</summary>
    private const string PageStartSelector = ".rz-body :is(h1, h2, h3, h4, h5, h6)";

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
        await Expect(_mainPage.AdminDivider).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Bug #2 regression: all sidebar menu items should be clickable and navigate to correct routes")]
    public async Task Sidebar_AllMenuItems_ShouldNavigateCorrectly()
    {
        var menuRoutes = new (string MenuText, string ExpectedPath)[]
        {
            ("Tasks", "/tasks"),
            ("Projects", "/projects"),
            ("Agent", "/agent"),
            ("Schedules", "/schedules"),
            ("Costs", "/costs"),
            ("Git Repositories", "/repositories"),
            ("PRD Generator", "/prd-generator"),
            ("Home", "/")
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

    /// <summary>
    ///     The footer names the product. Its version is the assembly's, checked by
    ///     <c>ShellBrowserTests.The_footer_shows_the_consoles_own_version</c>; this test pinned the old hardcoded v1.0.0.
    /// </summary>
    [Test]
    [Description("Footer should display platform info")]
    public async Task Footer_ShouldDisplay_PlatformInfo()
    {
        await NavigateToAsync("/").ConfigureAwait(false);
        await Expect(_mainPage.Footer).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_mainPage.Footer).ToContainTextAsync("Daedalus").ConfigureAwait(false);
    }
}
