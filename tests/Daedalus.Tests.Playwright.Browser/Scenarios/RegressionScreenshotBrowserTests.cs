using Daedalus.Tests.Playwright.Browser.PageObjects;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     Control-page screenshots for regression reports (the Agent page screenshot is taken by
///     <see cref="AgentPageBrowserTests"/> in its passing state). Each test asserts the page rendered and then saves a
///     full-page screenshot via <see cref="BrowserTestBase.SaveRegressionScreenshotAsync"/> — into the test work directory
///     by default, into <c>docs/regression-screenshots/</c> when <c>DAEDALUS_REGRESSION_SCREENSHOTS=1</c>.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Regression")]
[Description("Regression screenshots of the control pages (home, sessions, ralph config)")]
public class RegressionScreenshotBrowserTests : BrowserTestBase
{
    /// <summary>Sub-directory under <c>regression-screenshots/</c>; one folder per report date.</summary>
    private const string ReportFolder = "2026-08-16";

    [Test]
    [Description("Home dashboard renders and is captured")]
    public async Task Home_ShouldRender_AndCapture()
    {
        var page = new HomePage(Page, BaseUrl);
        await page.NavigateAsync().ConfigureAwait(false);
        await Expect(page.Title).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(page.StatsRow).ToBeVisibleAsync().ConfigureAwait(false);

        await SaveRegressionScreenshotAsync($"{ReportFolder}/home.png").ConfigureAwait(false);
    }

    [Test]
    [Description("Sessions page renders its grid and is captured")]
    public async Task Sessions_ShouldRender_AndCapture()
    {
        var page = new SessionsPage(Page, BaseUrl);
        await page.NavigateAsync().ConfigureAwait(false);
        await Expect(page.PageTitle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(page.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);

        await SaveRegressionScreenshotAsync($"{ReportFolder}/sessions.png").ConfigureAwait(false);
    }

    [Test]
    [Description("Ralph config page renders its form and is captured")]
    public async Task RalphConfig_ShouldRender_AndCapture()
    {
        var page = new RalphConfigPage(Page, BaseUrl);
        await page.NavigateAsync().ConfigureAwait(false);
        await Expect(page.PageTitle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(page.SaveButton)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);

        await SaveRegressionScreenshotAsync($"{ReportFolder}/ralph-config.png").ConfigureAwait(false);
    }
}
