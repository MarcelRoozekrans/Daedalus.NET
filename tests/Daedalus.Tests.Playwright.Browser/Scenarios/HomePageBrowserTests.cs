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
        if (!SetUpCompleted) return;
        _homePage = new HomePage(Page, BaseUrl);
    }

    [Test]
    [Description("Home page title should render with Radzen components")]
    public async Task HomePage_ShouldRender_Title()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.Title).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.Title).ToContainTextAsync("Daedalus Dashboard").ConfigureAwait(false);
    }

    [Test]
    [Description("Home page should display four stat cards")]
    public async Task HomePage_ShouldDisplay_FourStatCards()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.StatsRow).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.TotalTasksStat).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.SessionsStat).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.ActiveSessionsStat).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.CompletedTasksStat).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Stat cards should display text labels")]
    public async Task HomePage_StatCards_ShouldDisplayLabels()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.TotalTasksStat).ToContainTextAsync("Total Tasks").ConfigureAwait(false);
        await Expect(_homePage.SessionsStat).ToContainTextAsync("Execution Sessions").ConfigureAwait(false);
        await Expect(_homePage.ActiveSessionsStat).ToContainTextAsync("Active Sessions").ConfigureAwait(false);
        await Expect(_homePage.CompletedTasksStat).ToContainTextAsync("Completed Tasks").ConfigureAwait(false);
    }

    [Test]
    [Description("Quick actions card should display four action buttons")]
    public async Task HomePage_ShouldDisplay_QuickActions()
    {
        await _homePage.NavigateAsync().ConfigureAwait(false);
        await Expect(_homePage.QuickActionsCard).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.CreateTaskButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_homePage.ConfigureRalphButton).ToBeVisibleAsync().ConfigureAwait(false);
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
}
