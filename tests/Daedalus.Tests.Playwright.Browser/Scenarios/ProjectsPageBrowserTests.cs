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
        if (!SetUpCompleted)
            return;
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

    // ── Project selection ────────────────────────────────────────────────────

    [Test]
    [Description("Clicking project name should display the tasks panel")]
    public async Task ProjectsPage_ClickProjectName_ShouldDisplayTasksPanel()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);

        // Click the project name link
        var projectLink = _projectsPage.GetRowByText("Test Project").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Test Project" });
        await projectLink.ClickAsync().ConfigureAwait(false);

        // Tasks panel should appear below
        await Expect(_projectsPage.SelectedProjectPanel).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await Expect(_projectsPage.SelectedProjectPanel).ToContainTextAsync("Test Project").ConfigureAwait(false);
    }

    // ── Task count badge ─────────────────────────────────────────────────────

    [Test]
    [Description("Projects grid should display task count badges")]
    public async Task ProjectsPage_DataGrid_ShouldDisplay_TaskCountBadge()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        var badge = _projectsPage.GetRowByText("Test Project").Locator(".rz-badge");
        await Expect(badge).ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Delete confirmation ──────────────────────────────────────────────────

    [Test]
    [Description("Clicking Delete should open a confirmation dialog")]
    public async Task ProjectsPage_DeleteButton_ShouldOpenConfirmDialog()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await _projectsPage.GetDeleteButton("Test Project").ClickAsync().ConfigureAwait(false);
        await Expect(_projectsPage.ConfirmDialog).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.ConfirmDialog).ToContainTextAsync("Confirm Delete").ConfigureAwait(false);
    }

    [Test]
    [Description("Cancel button in delete confirmation should close dialog")]
    public async Task ProjectsPage_DeleteConfirmCancel_ShouldCloseDialog()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await _projectsPage.GetDeleteButton("Test Project").ClickAsync().ConfigureAwait(false);
        await Expect(_projectsPage.ConfirmDialog).ToBeVisibleAsync().ConfigureAwait(false);

        await _projectsPage.ConfirmCancelButton.ClickAsync().ConfigureAwait(false);
        await Expect(_projectsPage.ConfirmDialog).Not.ToBeVisibleAsync().ConfigureAwait(false);

        // Project should still exist
        await Expect(_projectsPage.GetRowByText("Test Project")).ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Edit dialog ──────────────────────────────────────────────────────────

    [Test]
    [Description("Clicking Edit should open edit dialog")]
    public async Task ProjectsPage_EditButton_ShouldOpenEditDialog()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await _projectsPage.GetEditButton("Test Project").ClickAsync().ConfigureAwait(false);
        await Expect(_projectsPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DialogTitle).ToContainTextAsync("Edit Project").ConfigureAwait(false);
    }

    // ── Create dialog ────────────────────────────────────────────────────────

    [Test]
    [Description("Create Project dialog should have a title")]
    public async Task ProjectsPage_CreateDialog_ShouldHaveTitle()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await _projectsPage.CreateProjectButton.ClickAsync().ConfigureAwait(false);
        await Expect(_projectsPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DialogTitle).ToContainTextAsync("Create New Project").ConfigureAwait(false);
    }
}
