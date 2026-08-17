using Daedalus.Tests.Playwright.Browser.PageObjects;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Executions")]
[Description("Executions page browser tests validating filter card and DataGrid rendering")]
public class ExecutionsPageBrowserTests : BrowserTestBase
{
    private ExecutionsPage _executionsPage = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted)
            return;
        _executionsPage = new ExecutionsPage(Page, BaseUrl);
    }

    [Test]
    [Description("Executions page should render title and filter card")]
    public async Task ExecutionsPage_ShouldRender_HeaderAndFilterCard()
    {
        await _executionsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_executionsPage.PageTitle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_executionsPage.FilterCard).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Filter card should have dropdown and search functionality")]
    public async Task ExecutionsPage_FilterCard_ShouldHaveDropdown()
    {
        await _executionsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_executionsPage.FilterDropDown).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Before filtering, should show info message")]
    public async Task ExecutionsPage_ShouldDisplay_InfoAlert_BeforeFiltering()
    {
        await _executionsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_executionsPage.InfoAlert).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("No error alert should be visible on initial load")]
    public async Task ExecutionsPage_ShouldNotDisplay_ErrorAlert()
    {
        await _executionsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_executionsPage.ErrorAlert).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Filter interaction ───────────────────────────────────────────────────

    [Test]
    [Description("Selecting a filter type should reveal the ID input and Search button")]
    public async Task ExecutionsPage_SelectFilter_ShouldShowInputAndSearch()
    {
        await _executionsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_executionsPage.FilterDropDown).ToBeVisibleAsync().ConfigureAwait(false);

        // Before selecting filter, Search button should not be visible
        await Expect(_executionsPage.SearchButton).Not.ToBeVisibleAsync().ConfigureAwait(false);

        // Click dropdown and select "Task ID"
        await _executionsPage.FilterDropDown.ClickAsync().ConfigureAwait(false);
        await Page.GetByText("Task ID", new PageGetByTextOptions { Exact = true })
            .ClickAsync().ConfigureAwait(false);

        // Now the ID input and Search button should appear
        await Expect(_executionsPage.FilterTextInput).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_executionsPage.SearchButton).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Info alert should tell user to select a filter type")]
    public async Task ExecutionsPage_InfoAlert_ShouldShowInstructions()
    {
        await _executionsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_executionsPage.InfoAlert).ToContainTextAsync("Select a filter").ConfigureAwait(false);
    }

    [Test]
    [Description("Selecting Session ID filter should also show input and Search")]
    public async Task ExecutionsPage_SelectSessionFilter_ShouldShowInputAndSearch()
    {
        await _executionsPage.NavigateAsync().ConfigureAwait(false);
        await _executionsPage.FilterDropDown.ClickAsync().ConfigureAwait(false);
        await Page.GetByText("Session ID", new PageGetByTextOptions { Exact = true })
            .ClickAsync().ConfigureAwait(false);

        await Expect(_executionsPage.FilterTextInput).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_executionsPage.SearchButton).ToBeVisibleAsync().ConfigureAwait(false);
    }
}
