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

    // ── On the shared projects feature ───────────────────────────────────────

    /// <summary>
    ///     A failed load shows the error and the empty state, as the page always did, never the list a page opened before
    ///     left in the shared feature.
    ///     The empty state and the grid are the two branches of one if, so the empty state showing is the grid hidden.
    ///     Red: show the alert only for an error over 100000 characters; the alert is missing.
    ///     Red: render the grid from ProjectsState.Projects instead of Current; the held list shows, not the empty state.
    /// </summary>
    [Test]
    [Description("A failed load shows the error and the empty state, not a held list")]
    public async Task ProjectsPage_FailedLoad_ShowsTheErrorAndTheEmptyState()
    {
        var menu = new MainPage(Page, BaseUrl);
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await _projectsPage.GetRowByText("Test Project").WaitForAsync().ConfigureAwait(false);
        await menu.TasksMenuItem.ClickAsync().ConfigureAwait(false);
        await Page.WaitForURLAsync("**/tasks").ConfigureAwait(false);

        await Page.RouteAsync("**/api/projects?*", route => route.FulfillAsync(new RouteFulfillOptions { Status = 500 })).ConfigureAwait(false);
        await menu.ProjectsMenuItem.ClickAsync().ConfigureAwait(false);

        await Expect(_projectsPage.ErrorAlert).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_projectsPage.EmptyStateTitle).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Deleting the project whose tasks show stops showing them. The delete is answered by a stub, so the seeded
    ///     project stays for the other tests and the list re-read still shows it.
    ///     Red: drop the ProjectSelectLoaded dispatch from LoadSelectedAsync; the tasks panel never shows.
    ///     Red: drop the "Project deleted" toast from the page's delete; the notification is missing.
    ///     Red: leave Selected in the ProjectSaved reducer for a delete; the tasks panel stays.
    /// </summary>
    [Test]
    [Description("Deleting the shown project hides its tasks panel")]
    public async Task ProjectsPage_DeletingTheShownProject_HidesItsTasksPanel()
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_projectsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await _projectsPage.GetRowByText("Test Project")
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Test Project" }).ClickAsync().ConfigureAwait(false);
        await Expect(_projectsPage.SelectedProjectPanel).ToBeVisibleAsync().ConfigureAwait(false);
        await Page.RouteAsync("**/api/projects/*", route => string.Equals(route.Request.Method, "DELETE", StringComparison.Ordinal)
            ? route.FulfillAsync(new RouteFulfillOptions { Status = 204 })
            : route.ContinueAsync()).ConfigureAwait(false);

        await _projectsPage.GetDeleteButton("Test Project").ClickAsync().ConfigureAwait(false);
        await _projectsPage.ConfirmDialog
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete", Exact = true }).ClickAsync().ConfigureAwait(false);

        await Expect(_projectsPage.Notification).ToContainTextAsync("Project deleted").ConfigureAwait(false);
        await Expect(_projectsPage.SelectedProjectPanel).ToBeHiddenAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     A refused create says why, in the words the dialog always used, and the dialog stays open.
    ///     Red: store a fixed "Failed to create" instead of the client's error; the server's reason is missing.
    ///     Red: close the dialog after the refusal's toast; the dialog is gone.
    /// </summary>
    [Test]
    [Description("A refused create says why and the dialog stays open")]
    public async Task ProjectsPage_RefusedCreate_SaysWhyAndStaysOpen()
    {
        await Page.RouteAsync("**/api/projects", route => string.Equals(route.Request.Method, "POST", StringComparison.Ordinal)
            ? route.FulfillAsync(new RouteFulfillOptions { Status = 400, Body = "Name taken" })
            : route.ContinueAsync()).ConfigureAwait(false);
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        await _projectsPage.CreateProjectButton.ClickAsync().ConfigureAwait(false);

        await _projectsPage.Dialog.Locator("input").First.FillAsync("Atlas").ConfigureAwait(false);
        await _projectsPage.Dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create" }).ClickAsync().ConfigureAwait(false);

        await Expect(_projectsPage.Notification).ToContainTextAsync("Failed to create project: Name taken").ConfigureAwait(false);
        await Expect(_projectsPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Every field in the Create and the Edit project dialog is named for a screen reader by its visible label, so a
    ///     role query by that label finds exactly one field.
    ///     Red: <c>Component</c> dropped from Project Name's form field; the label points at nothing and no textbox is
    ///     named "Project Name *".
    ///     Red: <c>Name</c> dropped from the Project Name textbox; its id is Radzen's random one and the label's for misses.
    ///     Red: <c>Component</c> dropped from Description's form field; no textbox is named Description.
    ///     Red: <c>Component</c> dropped from Version's form field; no textbox is named Version.
    ///     Each is red in the dialog it is dropped from: the two dialogs have their own markup.
    /// </summary>
    [TestCase("create")]
    [TestCase("edit")]
    [Description("Every project dialog field is named by its label")]
    public async Task Every_project_dialog_field_is_named_by_its_label(string dialog)
    {
        await _projectsPage.NavigateAsync().ConfigureAwait(false);
        if (string.Equals(dialog, "create", StringComparison.Ordinal))
        {
            await _projectsPage.CreateProjectButton.ClickAsync().ConfigureAwait(false);
        }
        else
        {
            await Expect(_projectsPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
            await _projectsPage.GetEditButton("Test Project").ClickAsync().ConfigureAwait(false);
        }

        await Expect(_projectsPage.Dialog.Locator("input").First).ToBeVisibleAsync().ConfigureAwait(false);

        var unnamed = new List<string>();
        foreach (var label in new[] { "Project Name *", "Description", "Version" })
        {
            var count = await _projectsPage.Dialog
                .GetByRole(AriaRole.Textbox, new LocatorGetByRoleOptions { Name = label, Exact = true })
                .CountAsync().ConfigureAwait(false);
            if (count != 1)
            {
                unnamed.Add($"{label} ({count} found)");
            }
        }

        string.Join("; ", unnamed).Should().BeEmpty($"every field in the {dialog} project dialog is named by its label:\n"
            + await _projectsPage.Dialog.AriaSnapshotAsync().ConfigureAwait(false));
    }
}
