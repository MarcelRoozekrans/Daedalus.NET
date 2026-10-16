using Daedalus.Tests.Playwright.Browser.PageObjects;
using Daedalus.Tests.Playwright.Browser.Utilities;

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
        if (!SetUpCompleted)
            return;
        _tasksPage = new TasksPage(Page, BaseUrl);
    }

    [Test]
    [Description("Tasks page should display page title and create button")]
    public async Task TasksPage_ShouldRender_HeaderAndCreateButton()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.PageTitle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.CreateTaskButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.CreateTaskButton).ToHaveTextAsync("New task").ConfigureAwait(false);
    }

    /// <summary>
    ///     An empty board says so in one muted sentence with the action, as the contract's empty state reads: "No tasks
    ///     yet." and New task, which opens the create dialog. The task list is the API's answer, fulfilled empty here.
    ///     Red: no <c>EmptyTemplate</c> on the grid, as before; Radzen's "No records to display." shows, not "No tasks yet.".
    ///     Red: the empty state's New task without a handler; the dialog does not open.
    /// </summary>
    [Test]
    [Description("An empty board says No tasks yet with New task")]
    public async Task An_empty_board_says_no_tasks_yet_with_new_task()
    {
        await Page.RouteAsync("**/api/tasks?*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = """{"items":[],"total":0,"page":1,"pageSize":15}""",
        })).ConfigureAwait(false);
        await _tasksPage.NavigateAsync().ConfigureAwait(false);

        var empty = _tasksPage.DataGrid.Locator("[data-testid='tasks-empty']");
        await Expect(empty).ToContainTextAsync("No tasks yet.").ConfigureAwait(false);

        await empty.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "New task", Exact = true }).ClickAsync()
            .ConfigureAwait(false);
        await Expect(_tasksPage.ProjectPicker).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     A board that is still loading is not empty: while the task list is held back, "No tasks yet." never shows,
    ///     not even for a frame before the first load starts. A watch made before the page loads records any moment it
    ///     showed. Once the list arrives empty, it shows.
    ///     Red: the template's <c>Loading: false</c> check dropped; the empty state is recorded while the load runs.
    ///     Red: the empty state never rendered; it does not show once the empty list arrives.
    ///     The template's <c>LoadRequested</c> check has no Red: line. With only it dropped the test stays green: the
    ///     page dispatches the load's start before its first render, so the initial state, not yet asked for, never
    ///     paints. The check stays as a guard for that order.
    /// </summary>
    [Test]
    [Description("A board that is loading does not say No tasks yet")]
    public async Task A_board_that_is_loading_does_not_say_no_tasks_yet()
    {
        var release = new TaskCompletionSource();
        async Task FulfillOnceReleasedAsync(IRoute route)
        {
            await release.Task.ConfigureAwait(false);
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = """{"items":[],"total":0,"page":1,"pageSize":15}""",
            }).ConfigureAwait(false);
        }

        // The handler returns at once and answers later, once released.
        var held = new List<Task>();
        await Page.RouteAsync("**/api/tasks?*", route =>
        {
            held.Add(FulfillOnceReleasedAsync(route));
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await TestAuth.WatchForAsync(Page, "[data-testid='tasks-empty']").ConfigureAwait(false);
        // Not the page object's navigation, which waits for the network to go idle: the held request never lets it.
        await NavigateToAsync("/tasks").ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await Page.WaitForTimeoutAsync(1000).ConfigureAwait(false);

        (await TestAuth.PathsThatShowedAsync(Page, "[data-testid='tasks-empty']").ConfigureAwait(false))
            .Should().BeEmpty("a board that is loading is not empty");

        release.SetResult();
        await Task.WhenAll(held).ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid.Locator("[data-testid='tasks-empty']")).ToContainTextAsync("No tasks yet.")
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     A board that could not be read is not empty: the error says why, and the grid does not claim "No tasks yet."
    ///     Red: the empty state rendered whatever the read said; "No tasks yet." shows under the error.
    /// </summary>
    [Test]
    [Description("A board that failed to load does not say No tasks yet")]
    public async Task A_board_that_failed_to_load_does_not_say_no_tasks_yet()
    {
        await Page.RouteAsync("**/api/tasks?*", route => route.FulfillAsync(new RouteFulfillOptions { Status = 500 }))
            .ConfigureAwait(false);
        await _tasksPage.NavigateAsync().ConfigureAwait(false);

        await Expect(_tasksPage.ErrorAlert.First).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid).Not.ToContainTextAsync("No tasks yet.").ConfigureAwait(false);
    }

    /// <summary>
    ///     A Manufacture still in flight when the person leaves the board reports nothing: leaving cancels the call, and
    ///     "Could not start: Request was cancelled" would be a failure that is only the page going away. The call is
    ///     held at the route and never reaches the server, so no run starts; a watch made before the page loads records
    ///     any moment the toast showed.
    ///     Red: drop the cancellation return before NotifyFailure in <c>ManufactureTask</c>; the toast is recorded.
    /// </summary>
    [Test]
    [Description("A Manufacture cut short by leaving the board shows no failure")]
    public async Task A_manufacture_cut_short_by_leaving_shows_no_failure()
    {
        var release = new TaskCompletionSource();
        var held = new List<Task>();
        async Task AbortOnceReleasedAsync(IRoute route)
        {
            await release.Task.ConfigureAwait(false);
            try
            {
                await route.AbortAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // The page already gave the request up when it left.
            }
        }

        await Page.RouteAsync("**/api/tasks/*/manufacture", route =>
        {
            held.Add(AbortOnceReleasedAsync(route));
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await TestAuth.WatchForAsync(Page, ".rz-notification-error", "Could not start").ConfigureAwait(false);
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        var manufacture = _tasksPage.DataGrid
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Manufacture", Exact = true }).First;
        var sent = Page.WaitForRequestAsync("**/api/tasks/*/manufacture");
        await manufacture.ClickAsync().ConfigureAwait(false);
        await sent.ConfigureAwait(false);

        await Page.EvaluateAsync("() => Blazor.navigateTo('/projects')").ConfigureAwait(false);
        await Page.WaitForURLAsync("**/projects").ConfigureAwait(false);
        await Page.WaitForTimeoutAsync(1500).ConfigureAwait(false);

        (await TestAuth.PathsThatShowedAsync(Page, ".rz-notification-error", "Could not start").ConfigureAwait(false))
            .Should().BeEmpty("a call cut short by leaving the page is not a failure to report");

        release.SetResult();
        await Task.WhenAll(held).ConfigureAwait(false);
    }

    /// <summary>
    ///     At 375px the header actions of Tasks and Projects keep their text on one line beside a caption that wraps.
    ///     Red: the Tasks header as before, with no wrap and no <c>white-space: nowrap</c>, the button squeezed beside
    ///     the caption; the text takes two lines.
    ///     Red: the same on Projects; "Create Project" takes two lines.
    /// </summary>
    [TestCase("/tasks", "btn-create-task")]
    [TestCase("/projects", "btn-create-project")]
    [Description("No header action wraps at 375px")]
    public async Task No_header_action_wraps_on_a_phone(string path, string testId)
    {
        await Page.SetViewportSizeAsync(375, 812).ConfigureAwait(false);
        await NavigateToAsync(path).ConfigureAwait(false);
        var button = Page.Locator($"[data-testid='{testId}']");
        await Expect(button).ToBeVisibleAsync().ConfigureAwait(false);

        var lines = await button.Locator(".rz-button-text").EvaluateAsync<int>(LinesScript).ConfigureAwait(false);
        lines.Should().Be(1, $"the {path} header action is one line");
    }

    /// <summary>The number of lines an element's text takes, from its text's line boxes.</summary>
    private const string LinesScript = """
        e => {
            const walker = document.createTreeWalker(e, NodeFilter.SHOW_TEXT);
            const tops = new Set();
            for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                if (!node.textContent.trim()) { continue; }
                const range = document.createRange();
                range.selectNodeContents(node);
                Array.from(range.getClientRects()).filter(r => r.width > 0).forEach(r => tops.add(Math.round(r.top)));
            }
            return tops.size;
        }
        """;

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

    /// <summary>
    ///     Manufacture starts a run, which the manufacture endpoint allows only the developer or admin role, so a task
    ///     manager's rows do not offer it and a developer's do. Edit shows for both, so the row has rendered its actions.
    ///     Red: _canStart set for any signed-in user, user.Identity is not null; the task manager's row holds it.
    ///     Red: _canStart never set, user.Identity is null; the developer's row lacks it.
    /// </summary>
    [TestCase("task-manager", false)]
    [TestCase("developer", true)]
    [Description("Manufacture shows only for a role that may start a run")]
    public async Task Manufacture_shows_only_for_a_role_that_may_start_a_run(string role, bool shown)
    {
        await TestRoles.WriteAsync(Context, BaseUrl, role).ConfigureAwait(false);
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.GetEditButton("Implement feature A")).ToBeVisibleAsync().ConfigureAwait(false);

        await Expect(_tasksPage.GetManufactureButton("Implement feature A")).ToHaveCountAsync(shown ? 1 : 0).ConfigureAwait(false);
    }

    /// <summary>
    ///     The icon-only row buttons are named for what they do, not for the icon's ligature text.
    ///     Red: drop the aria-label from the Manufacture button; the exact role-name lookup no longer finds it.
    /// </summary>
    [Test]
    [Description("The row action buttons have accessible names")]
    public async Task TasksPage_RowButtons_HaveAccessibleNames()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.GetManufactureButton("Implement feature A")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.GetManufactureButton("Implement feature A")).ToHaveAttributeAsync("aria-label", "Manufacture").ConfigureAwait(false);
        await Expect(_tasksPage.GetEditButton("Implement feature A")).ToHaveAttributeAsync("aria-label", "Edit task").ConfigureAwait(false);
        await Expect(_tasksPage.GetDeleteButton("Implement feature A")).ToHaveAttributeAsync("aria-label", "Delete task").ConfigureAwait(false);
    }

    /// <summary>
    ///     A pending task offers Manufacture. The seeded project has no repository URL, so the server refuses with 422 and
    ///     "The project has no repository URL.", and the page shows that reason. Red: show a generic message instead of the problem detail; the reason is
    ///     missing. Red: hide the button for status 0; it is not visible.
    /// </summary>
    [Test]
    [Description("A pending task offers Manufacture, and a refused start shows the server's reason")]
    public async Task TasksPage_Manufacture_OnAProjectWithoutARepositoryUrl_ShowsTheReason()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.GetStatusBadge("Implement feature A")).ToContainTextAsync("Pending").ConfigureAwait(false);

        await _tasksPage.GetManufactureButton("Implement feature A").ClickAsync().ConfigureAwait(false);

        await Expect(_tasksPage.Notification).ToContainTextAsync("no repository URL").ConfigureAwait(false);
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

    // ── Delete confirmation ──────────────────────────────────────────────────

    [Test]
    [Description("Clicking Delete should open a confirmation dialog")]
    public async Task TasksPage_DeleteButton_ShouldOpenConfirmDialog()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await _tasksPage.GetDeleteButton("Implement feature A").ClickAsync().ConfigureAwait(false);
        await Expect(_tasksPage.ConfirmDialog).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasksPage.ConfirmDialog).ToContainTextAsync("Confirm Delete").ConfigureAwait(false);
    }

    [Test]
    [Description("Cancel button in delete confirmation should close the dialog")]
    public async Task TasksPage_DeleteConfirmCancel_ShouldCloseDialog()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        await _tasksPage.GetDeleteButton("Implement feature A").ClickAsync().ConfigureAwait(false);
        await Expect(_tasksPage.ConfirmDialog).ToBeVisibleAsync().ConfigureAwait(false);

        await _tasksPage.ConfirmCancelButton.ClickAsync().ConfigureAwait(false);
        await Expect(_tasksPage.ConfirmDialog).Not.ToBeVisibleAsync().ConfigureAwait(false);

        // Task should still exist after cancelling
        await Expect(_tasksPage.GetRowByText("Implement feature A")).ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Priority badges ──────────────────────────────────────────────────────

    [Test]
    [Description("Task rows should display priority badges")]
    public async Task TasksPage_DataGrid_ShouldDisplay_PriorityBadges()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasksPage.DataGrid).ToBeVisibleAsync().ConfigureAwait(false);
        var badge = _tasksPage.GetPriorityBadge("Implement feature A");
        await Expect(badge).ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Create dialog content ────────────────────────────────────────────────

    [Test]
    [Description("Create Task dialog Cancel button should close dialog")]
    public async Task TasksPage_CreateDialogCancel_ShouldCloseDialog()
    {
        await _tasksPage.NavigateAsync().ConfigureAwait(false);
        await _tasksPage.CreateTaskButton.ClickAsync().ConfigureAwait(false);
        await Expect(_tasksPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);

        // Click the close/cancel button in the dialog
        var cancelButton = _tasksPage.Dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel" });
        await cancelButton.ClickAsync().ConfigureAwait(false);
        await Expect(_tasksPage.Dialog).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }
}
