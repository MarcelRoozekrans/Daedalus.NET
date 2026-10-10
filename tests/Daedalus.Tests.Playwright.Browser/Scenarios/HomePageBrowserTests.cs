using Daedalus.Tests.Playwright.Browser.PageObjects;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Home")]
[Description("Home page browser tests validating Radzen dashboard rendering")]
public class HomePageBrowserTests : BrowserTestBase
{
    private HomePage _homePage = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted)
            return;
        _homePage = new HomePage(Page, BaseUrl);
    }

    [Test]
    [Description("Home page title should render with Radzen components")]
    public async Task HomePage_ShouldRender_Title()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.Title).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.Title).ToContainTextAsync("Dashboard").ConfigureAwait(false);
    }

    [Test]
    [Description("Home page should display the task and project stat cards")]
    public async Task HomePage_ShouldDisplay_StatCards()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.StatsRow).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.TotalTasksStat).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.ProjectsStat).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.StatCards).ToHaveCountAsync(2).ConfigureAwait(false);
    }

    [Test]
    [Description("Stat cards should display text labels")]
    public async Task HomePage_StatCards_ShouldDisplayLabels()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.TotalTasksStat).ToContainTextAsync("Total Tasks").ConfigureAwait(false);
        await Expect(_homePage.ProjectsStat).ToContainTextAsync("Total Projects").ConfigureAwait(false);
    }

    [Test]
    [Description("Quick actions card should display four action buttons")]
    public async Task HomePage_ShouldDisplay_QuickActions()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.QuickActionsCard).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.CreateTaskButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.ViewCostsButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.GeneratePrdButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.ManageProjectsButton).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("No error alert should be visible on successful load")]
    public async Task HomePage_ShouldNotDisplay_ErrorAlert_OnSuccess()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.StatsRow).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.ErrorAlert).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Stat card interactions ──────────────────────────────────────────────

    [Test]
    [Description("Clicking Total Tasks stat card should navigate to Tasks page")]
    public async Task HomePage_ClickTotalTasksStat_ShouldNavigateToTasks()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.TotalTasksStat).ToBeVisibleAsync().ConfigureAwait(false);
        await _homePage.TotalTasksStat.ClickAsync().ConfigureAwait(false);
        await Page.WaitForURLAsync("**/tasks").ConfigureAwait(false);
        Page.Url.Should().Contain("/tasks");
    }

    // ── Quick action navigation ──────────────────────────────────────────────

    [Test]
    [Description("Clicking Create New Task should navigate to Tasks page")]
    public async Task HomePage_ClickCreateNewTask_ShouldNavigateToTasks()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.CreateTaskButton).ToBeVisibleAsync().ConfigureAwait(false);
        await _homePage.CreateTaskButton.ClickAsync().ConfigureAwait(false);
        await Page.WaitForURLAsync("**/tasks").ConfigureAwait(false);
        Page.Url.Should().Contain("/tasks");
    }

    [Test]
    [Description("Clicking Projects stat card should navigate to Projects page")]
    public async Task HomePage_ClickProjectsStat_ShouldNavigateToProjects()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.ProjectsStat).ToBeVisibleAsync().ConfigureAwait(false);
        await _homePage.ProjectsStat.ClickAsync().ConfigureAwait(false);
        await Page.WaitForURLAsync("**/projects").ConfigureAwait(false);
        Page.Url.Should().Contain("/projects");
    }

    [Test]
    [Description("Clicking View Costs should navigate to Costs page")]
    public async Task HomePage_ClickViewCosts_ShouldNavigateToCosts()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.ViewCostsButton).ToBeVisibleAsync().ConfigureAwait(false);
        await _homePage.ViewCostsButton.ClickAsync().ConfigureAwait(false);
        await Page.WaitForURLAsync("**/costs").ConfigureAwait(false);
        Page.Url.Should().Contain("/costs");
    }

    [Test]
    [Description("Clicking Generate PRD should navigate to PRD Generator page")]
    public async Task HomePage_ClickGeneratePrd_ShouldNavigateToPrdGenerator()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.GeneratePrdButton).ToBeVisibleAsync().ConfigureAwait(false);
        await _homePage.GeneratePrdButton.ClickAsync().ConfigureAwait(false);
        await Page.WaitForURLAsync("**/prd-generator").ConfigureAwait(false);
        Page.Url.Should().Contain("/prd-generator");
    }

    [Test]
    [Description("Clicking Manage Projects should navigate to Projects page")]
    public async Task HomePage_ClickManageProjects_ShouldNavigateToProjects()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.ManageProjectsButton).ToBeVisibleAsync().ConfigureAwait(false);
        await _homePage.ManageProjectsButton.ClickAsync().ConfigureAwait(false);
        await Page.WaitForURLAsync("**/projects").ConfigureAwait(false);
        Page.Url.Should().Contain("/projects");
    }

    // ── Content validation ───────────────────────────────────────────────────

    // ── Bug #1 regression: favicon ───────────────────────────────────────────

    [Test]
    [Description("Bug #1 regression: favicon.svg should load successfully (was favicon.png causing 404)")]
    public async Task Favicon_ShouldLoad_Without404()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);

        var faviconResponse = await Page.APIRequest.GetAsync(new Uri(BaseUrl, "/favicon.svg").ToString())
            .ConfigureAwait(false);
        faviconResponse.Status.Should().Be(200, "favicon.svg should be served successfully");
    }

    [Test]
    [Description("Bug #1 regression: index.html should reference favicon.svg, not favicon.png")]
    public async Task IndexHtml_ShouldReference_FaviconSvg()
    {
        var response = await Page.APIRequest.GetAsync(BaseUrl.ToString()).ConfigureAwait(false);
        var body = await response.TextAsync().ConfigureAwait(false);

        body.Should().Contain("favicon.svg", "index.html should reference the SVG favicon");
        body.Should().NotContain("favicon.png", "index.html should not reference the old PNG favicon");
    }
}
