using Daedalus.Tests.Playwright.Browser.PageObjects;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Projects")]
[Description("Projects page browser tests validating Radzen DataGrid rendering and CRUD")]
public class ProjectsPageBrowserTests : BrowserTestBase
{
    private ProjectsPage _projectsPage = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted) return;
        _projectsPage = new ProjectsPage(Page, BaseUrl);
    }

    [Test]
    [Description("Projects page should render title and create button")]
    public async Task ProjectsPage_ShouldRender_HeaderAndCreateButton()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_projectsPage.PageTitle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.CreateProjectButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.CreateProjectButton).ToContainTextAsync("Create Project").ConfigureAwait(false);
    }

    [Test]
    [Description("Projects page should display data grid with seeded project")]
    public async Task ProjectsPage_ShouldRender_DataGridWithProjects()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        // Wait for the DataGrid to appear (it's conditionally rendered when data loads)
        await Expect(_projectsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DataGridRows.First).ToBeVisibleAsync().ConfigureAwait(false);
        var rowCount = await _projectsPage.DataGridRows.CountAsync().ConfigureAwait(false);
        rowCount.Should().BeGreaterThanOrEqualTo(1, "Seeded data includes 1 project");
    }

    [Test]
    [Description("Projects data grid should show seeded project name")]
    public async Task ProjectsPage_DataGrid_ShouldDisplay_SeededProjectName()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.GetRowByText("Test Project")).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Clicking Create Project should open dialog")]
    public async Task ProjectsPage_CreateButton_ShouldOpenDialog()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await _projectsPage.CreateProjectButton.ClickAsync().ConfigureAwait(false);
        await Expect(_projectsPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Project rows should have edit and delete buttons")]
    public async Task ProjectsPage_DataGrid_ShouldDisplay_ActionButtons()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.GetEditButton("Test Project")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.GetDeleteButton("Test Project")).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("No error alert should be visible on successful load")]
    public async Task ProjectsPage_ShouldNotDisplay_ErrorAlert()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        // Wait for data to load before checking for error absence
        await Expect(_projectsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DataGridRows.First).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.ErrorAlert).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }
}
