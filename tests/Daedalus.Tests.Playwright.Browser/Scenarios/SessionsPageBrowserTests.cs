using Daedalus.Tests.Playwright.Browser.PageObjects;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Sessions")]
[Description("Sessions page browser tests validating Radzen DataGrid and toggle buttons")]
public class SessionsPageBrowserTests : BrowserTestBase
{
    private SessionsPage _sessionsPage = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted) return;
        _sessionsPage = new SessionsPage(Page, BaseUrl);
    }

    [Test]
    [Description("Sessions page should render title and toggle buttons")]
    public async Task SessionsPage_ShouldRender_HeaderAndToggle()
    {
        await _sessionsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_sessionsPage.PageTitle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_sessionsPage.AllSessionsButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_sessionsPage.ActiveOnlyButton).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Sessions page should display data grid")]
    public async Task SessionsPage_ShouldRender_DataGrid()
    {
        await _sessionsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_sessionsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Active Only button should toggle filter")]
    public async Task SessionsPage_ActiveToggle_ShouldFilterSessions()
    {
        await _sessionsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_sessionsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await _sessionsPage.ActiveOnlyButton.ClickAsync().ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);
        await _sessionsPage.AllSessionsButton.ClickAsync().ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);
        await Expect(_sessionsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("No error alert on successful load")]
    public async Task SessionsPage_ShouldNotDisplay_ErrorAlert()
    {
        await _sessionsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_sessionsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_sessionsPage.ErrorAlert).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }
}
