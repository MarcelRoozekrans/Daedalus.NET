using Daedalus.Tests.Playwright.Browser.PageObjects;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Tasks")]
[Description("Tasks page browser tests validating Radzen DataGrid rendering and CRUD operations")]
public class TasksPageBrowserTests : BrowserTestBase
{
    private TasksPage _tasksPage = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted) return;
        _tasksPage = new TasksPage(Page, BaseUrl);
    }

    [Test]
    [Description("Tasks page should display page title and create button")]
    public async Task TasksPage_ShouldRender_HeaderAndCreateButton()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.PageTitle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.CreateTaskButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.CreateTaskButton).ToContainTextAsync("Create Task").ConfigureAwait(false);
    }

    [Test]
    [Description("Tasks page should display data grid with seeded tasks")]
    public async Task TasksPage_ShouldRender_DataGridWithTasks()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DataGridRows.First).ToBeVisibleAsync().ConfigureAwait(false);

        var rowCount = await _tasksPage.DataGridRows.CountAsync().ConfigureAwait(false);
        rowCount.Should().BeGreaterThanOrEqualTo(2, "Seeded data includes 2 tasks");
    }

    [Test]
    [Description("Tasks data grid should show seeded task titles")]
    public async Task TasksPage_DataGrid_ShouldDisplay_SeededTaskTitles()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.GetRowByText("Implement feature A")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.GetRowByText("Fix bug B")).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Task rows should display status badges")]
    public async Task TasksPage_DataGrid_ShouldDisplay_StatusBadges()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        var badge = _tasksPage.GetStatusBadge("Implement feature A");
        await Expect(badge).ToContainTextAsync("Pending").ConfigureAwait(false);
    }

    [Test]
    [Description("Task rows should have edit and delete action buttons")]
    public async Task TasksPage_DataGrid_ShouldDisplay_ActionButtons()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.GetEditButton("Implement feature A")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.GetDeleteButton("Implement feature A")).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Clicking Create Task should open a dialog")]
    public async Task TasksPage_CreateButton_ShouldOpenDialog()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await _tasksPage.CreateTaskButton.ClickAsync().ConfigureAwait(false);
        await Expect(_tasksPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DialogTitle).ToContainTextAsync("Create New Task").ConfigureAwait(false);
    }

    [Test]
    [Description("Clicking Edit button should open edit dialog")]
    public async Task TasksPage_EditButton_ShouldOpenEditDialog()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await _tasksPage.GetEditButton("Implement feature A").ClickAsync().ConfigureAwait(false);
        await Expect(_tasksPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DialogTitle).ToContainTextAsync("Edit Task").ConfigureAwait(false);
    }

    [Test]
    [Description("No error alert should be visible on successful load")]
    public async Task TasksPage_ShouldNotDisplay_ErrorAlert()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        // Wait for data to load before checking for error absence
        await Expect(_tasksPage.DataGridRows.First).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.ErrorAlert).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }
}
