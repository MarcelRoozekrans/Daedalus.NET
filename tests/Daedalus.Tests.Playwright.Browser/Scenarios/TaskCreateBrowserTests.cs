using System.Text.RegularExpressions;
using Daedalus.Tests.Playwright.Browser.Fixtures;
using Daedalus.Tests.Playwright.Browser.PageObjects;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     Phase 2.13, #348: creating a task works first time, Create and manufacture starts it in one step, and the Tasks grid
///     shows the contract's columns. The fixture seeds two projects: Test Project, with no repository URL, so a manufacture
///     of its tasks is refused for real; and Widgets, whose repository URL no allow-list entry matches.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Tasks")]
[Description("Creating tasks and the Tasks grid's columns")]
public class TaskCreateBrowserTests : BrowserTestBase
{
    private const string TestProject = "Test Project";

    private TasksPage _tasks = null!;

    private static RunSeeds Runs => E2EServerFixture.Runs;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted)
            return;
        _tasks = new TasksPage(Page, BaseUrl);
    }

    private static string Unique(string what) => $"{what} {Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    ///     A task needs only a project, a title and a description: no prompt. Create closes the dialog and the new task is
    ///     the grid's first row.
    ///     Red: the dialog refuses a blank prompt, as before; the dialog stays open.
    ///     Red: the board reloads only after a refused manufacture; the first row is another task.
    /// </summary>
    [Test]
    [Description("Creating a task with a project, a title and a description succeeds")]
    public async Task Creating_a_task_with_project_title_and_description_succeeds()
    {
        var title = Unique("Created task");
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);
        await _tasks.FillNewTaskAsync(TestProject, title, "Only a description, no prompt").ConfigureAwait(false);

        await _tasks.CreateButton.ClickAsync().ConfigureAwait(false);

        await Expect(_tasks.Dialog).ToBeHiddenAsync().ConfigureAwait(false);
        await Expect(_tasks.DataGridRows.First).ToContainTextAsync(title).ConfigureAwait(false);
    }

    /// <summary>
    ///     Create and manufacture creates the task, starts the new task's run and opens it. The real starter would clone the
    ///     allow-listed GitHub repository into a worktree, which this fixture cannot do, so only the manufacture call is
    ///     answered by the test, with the seeded Running run; the create is real.
    ///     Red: the page navigates to <c>runs</c> without the id; the URL is not the run's.
    ///     Red: the dialog manufactures the project's id instead of the new task's; the request names another id.
    /// </summary>
    [Test]
    [Description("Create and manufacture opens the new run")]
    public async Task Create_and_manufacture_opens_the_new_run()
    {
        var runId = Runs.Running;
        string? manufactured = null;
        await Page.RouteAsync("**/api/tasks/*/manufacture", async route =>
        {
            manufactured = route.Request.Url;
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 201,
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { runId }),
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var title = Unique("Manufactured task");
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);
        await _tasks.FillNewTaskAsync(TestProject, title, "Create it and start it").ConfigureAwait(false);

        var created = Page.WaitForResponseAsync(r =>
            string.Equals(r.Request.Method, "POST", StringComparison.Ordinal) && r.Url.EndsWith("/api/tasks", StringComparison.Ordinal));
        await _tasks.CreateAndManufactureButton.ClickAsync().ConfigureAwait(false);
        var createdTask = await (await created.ConfigureAwait(false)).JsonAsync().ConfigureAwait(false);
        var taskId = createdTask!.Value.GetProperty("id").GetString();

        await Expect(Page).ToHaveURLAsync(new Regex($"/runs/{runId}$")).ConfigureAwait(false);
        manufactured.Should().EndWith($"/api/tasks/{taskId}/manufacture");
    }

    /// <summary>
    ///     A refusal names the first failed rule in the dialog's alert, which only an open dialog can show, and the form
    ///     keeps its values. A visibility check right after the click would pass before the answer arrives, so the alert
    ///     stands for the dialog staying open.
    ///     Red: the refusal goes to a toast instead; the dialog alert is missing.
    ///     Red: the dialog closes on a refusal; the dialog alert is missing.
    ///     Red: a refusal clears the title; the title box is empty.
    /// </summary>
    [Test]
    [Description("A refused create shows the reason and keeps the dialog open")]
    public async Task A_refused_create_shows_the_reason_and_keeps_the_dialog_open()
    {
        var title = new string('x', 501);
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);
        await _tasks.FillNewTaskAsync(TestProject, title, "Too long a title").ConfigureAwait(false);

        await _tasks.CreateButton.ClickAsync().ConfigureAwait(false);

        await Expect(_tasks.DialogAlert).ToContainTextAsync("Title cannot exceed 500 characters").ConfigureAwait(false);
        await Expect(_tasks.TitleInput).ToHaveValueAsync(title).ConfigureAwait(false);
    }

    /// <summary>
    ///     The edit dialog has the same alert: a refused save names the first failed rule there, and the form keeps its
    ///     values. Its project is shown and cannot change.
    ///     Red: the edit dialog sends a refusal to a toast instead; the dialog alert is missing.
    ///     Red: the edit dialog clears the title on a refusal; the title box is empty.
    ///     Red: the edit dialog's picker left enabled; it is not disabled.
    /// </summary>
    [Test]
    [Description("A refused edit shows the reason in the dialog")]
    public async Task A_refused_edit_shows_the_reason_in_the_dialog()
    {
        var title = new string('y', 501);
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.GetEditButton("Fix bug B").ClickAsync().ConfigureAwait(false);
        await _tasks.TitleInput.FillAsync(title).ConfigureAwait(false);

        await _tasks.Dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save changes", Exact = true })
            .ClickAsync().ConfigureAwait(false);

        await Expect(_tasks.DialogAlert).ToContainTextAsync("Title cannot exceed 500 characters").ConfigureAwait(false);
        await Expect(_tasks.TitleInput).ToHaveValueAsync(title).ConfigureAwait(false);
        await Expect(_tasks.ProjectPicker).ToHaveClassAsync(new Regex("rz-state-disabled")).ConfigureAwait(false);
    }

    /// <summary>
    ///     Test Project has no repository URL, so the server refuses the manufacture after the task was created. The dialog
    ///     closes onto the board, which names the reason; the task is on the board and still offers Manufacture.
    ///     Red: the dialog closes only when the run started; it stays open.
    ///     Red: the alert is set only for a signing-in reason; the board's alert is missing.
    ///     Red: the board reloads only after a plain Create; the new task's row and its Manufacture are missing.
    /// </summary>
    [Test]
    [Description("A refused manufacture after create keeps the task and says why")]
    public async Task A_refused_manufacture_after_create_keeps_the_task_and_says_why()
    {
        var title = Unique("Refused task");
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);
        await _tasks.FillNewTaskAsync(TestProject, title, "Its project has no repository").ConfigureAwait(false);

        await _tasks.CreateAndManufactureButton.ClickAsync().ConfigureAwait(false);

        await Expect(_tasks.Dialog).ToBeHiddenAsync().ConfigureAwait(false);
        await Expect(_tasks.ManufactureRefusedAlert).ToContainTextAsync("no repository URL").ConfigureAwait(false);
        await Expect(_tasks.GetManufactureButton(title)).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Each option names the project and, as secondary text, its repository, or says it has none.
    ///     Red: the option template shows the name only; the repository is missing.
    ///     Red: a project without a repository shows an empty line; "No repository" is missing.
    /// </summary>
    [Test]
    [Description("The project picker shows name and repository")]
    public async Task The_project_picker_shows_name_and_repository()
    {
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);
        await _tasks.ProjectPicker.ClickAsync().ConfigureAwait(false);

        await Expect(_tasks.ProjectOption(RunSeeds.RepositoryProjectName)).ToContainTextAsync("github.com/acme/widgets").ConfigureAwait(false);
        await Expect(_tasks.ProjectOption(TestProject)).ToContainTextAsync("No repository").ConfigureAwait(false);
    }

    /// <summary>
    ///     Manufacture shows its text beside its icon, and its accessible name is exactly "Manufacture".
    ///     Red: the button keeps its icon only; the text label is missing.
    ///     Red: the aria-label removed, so the name is the icon ligature and the text; the exact-name lookup finds nothing.
    /// </summary>
    [Test]
    [Description("The manufacture button has its accessible name")]
    public async Task The_manufacture_button_has_its_accessible_name()
    {
        await _tasks.NavigateAsync().ConfigureAwait(false);

        await Expect(_tasks.GetManufactureLabel("Implement feature A")).ToHaveTextAsync("Manufacture").ConfigureAwait(false);
    }

    /// <summary>
    ///     At desktop width the grid has exactly the contract's columns: no Prompt and no Iterations.
    ///     Red: a Prompt column added back; it has a header.
    ///     Red: an Iterations column added back; it has a header.
    ///     Red: the Pull request column titled "PR"; its header is missing.
    ///     Red: the Project column not visible; its header is missing.
    ///     Red: Priority's <c>Visible</c> inverted; at 1280 it is hidden.
    ///     Red: Created's <c>Visible</c> inverted; at 1280 it is hidden.
    ///     Red: the column titled "Updated" again; there is no Created header.
    /// </summary>
    [Test]
    [Description("Tasks shows no prompt or iterations column")]
    public async Task Tasks_shows_no_prompt_or_iterations_column()
    {
        await Page.SetViewportSizeAsync(1280, 800).ConfigureAwait(false);
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasks.DataGridRows.First).ToBeVisibleAsync().ConfigureAwait(false);

        await Expect(_tasks.Header("Prompt")).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(_tasks.Header("Iterations")).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(_tasks.Header("Pull request")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasks.Header("Project")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasks.Header("Priority")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasks.Header("Created")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasks.Header("Updated")).ToHaveCountAsync(0).ConfigureAwait(false);
    }

    /// <summary>
    ///     A task whose run opened a pull request links it as "#N"; a task with none shows a dash. The Project column names
    ///     the task's project.
    ///     Red: the link text is the whole URL; it is not "#42".
    ///     Red: the link points at the run instead of the pull request; the href is not the PR's.
    ///     Red: a task without a pull request shows an empty cell; the dash is missing.
    ///     Red: the Project column shows the project id; the name is missing.
    /// </summary>
    [Test]
    [Description("The pull request column links the task's pull request")]
    public async Task The_pull_request_column_links_the_tasks_pull_request()
    {
        await Page.SetViewportSizeAsync(1280, 800).ConfigureAwait(false);
        await _tasks.NavigateAsync().ConfigureAwait(false);

        var link = _tasks.GetPullRequestCell(RunSeeds.PullRequestTaskTitle);
        await Expect(link).ToHaveTextAsync("#42").ConfigureAwait(false);
        await Expect(link).ToHaveAttributeAsync("href", RunSeeds.PullRequestUrl).ConfigureAwait(false);
        await Expect(_tasks.GetPullRequestCell("Fix bug B")).ToHaveTextAsync("—").ConfigureAwait(false);
        await Expect(_tasks.GetRowByText(RunSeeds.PullRequestTaskTitle)).ToContainTextAsync(RunSeeds.RepositoryProjectName).ConfigureAwait(false);
    }

    /// <summary>
    ///     At tablet width, 768×1024, the grid drops Priority and Created and keeps the rest.
    ///     Red: Priority always visible; at 768 it shows.
    ///     Red: Created always visible; at 768 it shows.
    ///     Red: Pull request bound to the desktop query; at 768 it is hidden.
    ///     Red: Status bound to the desktop query; at 768 it is hidden.
    /// </summary>
    [Test]
    [Description("At tablet width tasks hides priority and created")]
    public async Task At_tablet_width_tasks_hides_priority_and_created()
    {
        await Page.SetViewportSizeAsync(768, 1024).ConfigureAwait(false);
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await Expect(_tasks.DataGridRows.First).ToBeVisibleAsync().ConfigureAwait(false);

        await Expect(_tasks.Header("Priority")).ToBeHiddenAsync().ConfigureAwait(false);
        await Expect(_tasks.Header("Created")).ToBeHiddenAsync().ConfigureAwait(false);
        await Expect(_tasks.Header("Pull request")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_tasks.Header("Status")).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     On a phone, 375×812, a task's status and its Manufacture button are on screen without scrolling the grid
    ///     sideways, and the title has the grid's width. The grid is one column there: under the title, the status badge
    ///     and the pull request, then the actions. Project, which the contract drops first, is not shown.
    ///     Red: Project not bound to the phone query; its header shows at 375.
    ///     Red: the actions left in their column on a phone, out of the title cell, and the grid's minimum width kept;
    ///     Manufacture's right edge is at 863px.
    ///     Red: the status left in its column on a phone, out of the title cell, and the minimum width kept; the badge's
    ///     right edge is at 826px.
    ///     Red: the grid's minimum width kept on a phone; the wrapper scrolls sideways by 585px.
    ///     Red: Pull request not bound to the phone query; the title is 173px of the grid's 295px. Leaving Actions or
    ///     Status in their columns without the minimum width fails here too, at 63px and 123px.
    /// </summary>
    [Test]
    [Description("On a phone the status and Manufacture show without scrolling sideways")]
    public async Task On_a_phone_status_and_manufacture_show_without_scrolling_sideways()
    {
        await Page.SetViewportSizeAsync(375, 812).ConfigureAwait(false);
        await _tasks.NavigateAsync().ConfigureAwait(false);
        var row = _tasks.GetRowByText("Fix bug B");
        var manufacture = _tasks.GetManufactureButton("Fix bug B");
        await Expect(manufacture).ToBeVisibleAsync().ConfigureAwait(false);

        await Expect(_tasks.Header("Project")).ToHaveCountAsync(0).ConfigureAwait(false);
        var badge = (await _tasks.GetRunStatusBadge("Fix bug B").BoundingBoxAsync().ConfigureAwait(false))!;
        var button = (await manufacture.BoundingBoxAsync().ConfigureAwait(false))!;
        (button.X + button.Width).Should().BeLessThanOrEqualTo(375, "Manufacture is on screen");
        (badge.X + badge.Width).Should().BeLessThanOrEqualTo(375, "the status is on screen");

        var scroll = await Page.Locator(".tasks-grid").EvaluateAsync<int>("e => e.scrollWidth - e.clientWidth").ConfigureAwait(false);
        scroll.Should().BeLessThanOrEqualTo(0, "the grid does not scroll sideways");

        var title = (await row.Locator("td").First.BoundingBoxAsync().ConfigureAwait(false))!;
        var grid = (await Page.Locator(".tasks-grid").BoundingBoxAsync().ConfigureAwait(false))!;
        title.Width.Should().BeApproximately(grid.Width, 2, "the title has the grid's width");
    }

    /// <summary>
    ///     On a phone, 375×812, the create dialog spans the screen and its buttons stack: Create and manufacture on top,
    ///     then Create, then Cancel, each as wide as the others. Radzen itself makes a dialog full width at 768px and below.
    ///     Red: the dialog's Style forces <c>width: 700px !important</c>; it is 700 wide.
    ///     Red: no <c>flex-direction</c> in the phone rule, so the buttons stay in a row; they share one top.
    ///     Red: <c>column</c> instead of <c>column-reverse</c>; Cancel is on top.
    ///     Red: Cancel given <c>order: 1</c> in the phone rule; Cancel is on top of Create.
    ///     Red: <c>align-items: flex-end</c> in the phone rule; Create is narrower than Create and manufacture.
    ///     Red: Cancel alone given <c>align-self: flex-end</c>; Cancel is narrower.
    /// </summary>
    [Test]
    [Description("On a phone the create dialog is full width with stacked buttons")]
    public async Task On_a_phone_the_create_dialog_is_full_width_with_stacked_buttons()
    {
        await Page.SetViewportSizeAsync(375, 812).ConfigureAwait(false);
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);

        var dialog = (await _tasks.DialogBox.BoundingBoxAsync().ConfigureAwait(false))!;
        dialog.Width.Should().BeInRange(374, 375, "the dialog spans the 375px screen");

        var primary = (await _tasks.CreateAndManufactureButton.BoundingBoxAsync().ConfigureAwait(false))!;
        var create = (await _tasks.CreateButton.BoundingBoxAsync().ConfigureAwait(false))!;
        var cancel = (await _tasks.DialogCancelButton.BoundingBoxAsync().ConfigureAwait(false))!;

        primary.Y.Should().BeLessThan(create.Y, "Create and manufacture is on top");
        create.Y.Should().BeLessThan(cancel.Y, "Create is above Cancel");
        create.Width.Should().BeApproximately(primary.Width, 1, "the stacked buttons are equally wide");
        cancel.Width.Should().BeApproximately(primary.Width, 1, "the stacked buttons are equally wide");
    }

    /// <summary>
    ///     A task whose run is parked at the human gate shows the badge the Runs page shows for that run: the same text,
    ///     "Awaiting approval", and the same classes, Warning, solid and pill-shaped.
    ///     Red: the mapper drops the run's AwaitingSignal; the task reads "Waiting: ".
    ///     Red: Tasks renders its own Warning badge in the lighter shade; its classes differ from the Runs badge's.
    /// </summary>
    [Test]
    [Description("A task whose run awaits approval reads as Runs reads it")]
    public async Task A_task_whose_run_awaits_approval_reads_as_runs_reads_it()
    {
        await Page.SetViewportSizeAsync(1280, 800).ConfigureAwait(false);
        var runs = new RunsPage(Page, BaseUrl);
        await runs.NavigateAsync().ConfigureAwait(false);
        var runBadge = runs.RowFor(Runs.AwaitingApproval).Locator("[data-testid='run-status']");
        await Expect(runBadge).ToHaveTextAsync("Awaiting approval").ConfigureAwait(false);
        var runClasses = await runBadge.GetAttributeAsync("class").ConfigureAwait(false);
        runClasses.Should().Contain("rz-badge-warning").And.Contain("rz-badge-pill");

        await _tasks.NavigateAsync().ConfigureAwait(false);
        var taskBadge = _tasks.GetRunStatusBadge(RunSeeds.AwaitingTaskTitle);

        await Expect(taskBadge).ToHaveTextAsync("Awaiting approval").ConfigureAwait(false);
        await Expect(taskBadge).ToHaveAttributeAsync("class", runClasses!).ConfigureAwait(false);
    }

    /// <summary>
    ///     A task from the retired loop, stored Completed with no run, shows its own status muted, not "Pending": its
    ///     history is not hidden.
    ///     Red: NoRunText returns "Pending" for every task with no run; the badge reads Pending.
    ///     Red: NoRunStyle Success; the badge is not Light.
    /// </summary>
    [Test]
    [Description("A task with no run and a stored Completed status shows Completed")]
    public async Task A_task_with_no_run_and_a_stored_completed_status_shows_completed()
    {
        await _tasks.NavigateAsync().ConfigureAwait(false);
        var badge = _tasks.GetRunStatusBadge(RunSeeds.LegacyTaskTitle);

        await Expect(badge).ToHaveTextAsync("Completed").ConfigureAwait(false);
        await Expect(badge).ToHaveClassAsync(new Regex(@"\brz-badge-light\b")).ConfigureAwait(false);
    }

    /// <summary>
    ///     While a create is in flight the dialog cannot be closed: Escape and the close button do nothing, so a task is
    ///     never created behind a closed dialog that the board does not show. The create is slowed to make the window.
    ///     Red: no hold before the create; Escape closes the dialog.
    ///     Red: the hold stops Escape only; the close button closes the dialog.
    ///     Red: the board reloads only after a refused manufacture; the new task is not the first row.
    /// </summary>
    [Test]
    [Description("The create dialog cannot be closed while it submits")]
    public async Task The_create_dialog_cannot_be_closed_while_it_submits()
    {
        await Page.RouteAsync("**/api/tasks", async route =>
        {
            if (string.Equals(route.Request.Method, "POST", StringComparison.Ordinal))
            {
                await Task.Delay(2000).ConfigureAwait(false);
            }

            await route.ContinueAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        var title = Unique("Held task");
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);
        await _tasks.FillNewTaskAsync(TestProject, title, "Created while Escape is pressed").ConfigureAwait(false);
        // Radzen arms Escape on a dialog 500ms after it opens, and ignores Escape while any popup is still showing, so the
        // test waits until Escape would close the dialog; otherwise the first assertion could pass without the hold.
        await Page.WaitForFunctionAsync(
                "() => document.querySelector('.rz-dialog-content').options !== undefined"
                + " && Array.from(document.querySelectorAll('.rz-popup,.rz-overlaypanel')).every(p => p.style.display === 'none')")
            .ConfigureAwait(false);

        await _tasks.CreateButton.ClickAsync().ConfigureAwait(false);
        await Page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await Page.WaitForTimeoutAsync(400).ConfigureAwait(false);
        (await _tasks.Dialog.IsVisibleAsync().ConfigureAwait(false)).Should().BeTrue("Escape does not close a submitting dialog");

        // The held close button is aria-disabled, which Playwright's actionability check would wait out; a person can
        // still click it, so the click is forced.
        await _tasks.DialogClose.ClickAsync(new LocatorClickOptions { Force = true }).ConfigureAwait(false);
        await Page.WaitForTimeoutAsync(400).ConfigureAwait(false);
        (await _tasks.Dialog.IsVisibleAsync().ConfigureAwait(false)).Should().BeTrue("the close button does not close a submitting dialog");

        await Expect(_tasks.DataGridRows.First).ToContainTextAsync(title).ConfigureAwait(false);
    }

    /// <summary>
    ///     With no projects the create dialog says so and links to Projects, as the contract's empty state does. The
    ///     projects list is answered empty by the test; the fixture always has projects.
    ///     Red: no empty state under the picker; the message is missing.
    ///     Red: the link points at the board; its href is not projects.
    /// </summary>
    [Test]
    [Description("With no projects the create dialog says so and links to Projects")]
    public async Task With_no_projects_the_create_dialog_says_so()
    {
        await Page.RouteAsync("**/api/projects?*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = """{"items":[],"total":0,"page":1,"pageSize":200}""",
        })).ConfigureAwait(false);
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);

        await Expect(_tasks.NoProjects).ToContainTextAsync("No projects yet.").ConfigureAwait(false);
        await Expect(_tasks.NoProjects.Locator("a")).ToHaveAttributeAsync("href", "projects").ConfigureAwait(false);
    }

    /// <summary>
    ///     A projects list that cannot be read says why under the create dialog's picker and on the board.
    ///     Red: TaskFields shows the error only during a sign-in; the message under the picker is missing.
    ///     Red: the board shows its projects alert only during a sign-in; it is missing.
    /// </summary>
    [Test]
    [Description("A failed projects load says why in the create dialog")]
    public async Task A_failed_projects_load_says_why_in_the_create_dialog()
    {
        await Page.RouteAsync("**/api/projects?*", route => route.FulfillAsync(new RouteFulfillOptions { Status = 500 })).ConfigureAwait(false);
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);

        await Expect(_tasks.ProjectError).ToContainTextAsync("The projects could not be loaded").ConfigureAwait(false);
        await Expect(Page.Locator("[data-testid='tasks-projects-error']")).ToContainTextAsync("The projects could not be loaded").ConfigureAwait(false);
    }

    /// <summary>
    ///     The edit dialog's project that cannot be read says why under the locked picker, instead of an empty picker.
    ///     Red: the edit dialog drops the failure's reason, so the picker is just empty, as before; the message is missing.
    /// </summary>
    [Test]
    [Description("A failed project read says why in the edit dialog")]
    public async Task A_failed_project_read_says_why_in_the_edit_dialog()
    {
        await Page.RouteAsync("**/api/projects/*", route => route.FulfillAsync(new RouteFulfillOptions { Status = 500 })).ConfigureAwait(false);
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.GetEditButton("Fix bug B").ClickAsync().ConfigureAwait(false);

        await Expect(_tasks.ProjectError).ToContainTextAsync("The projects could not be loaded").ConfigureAwait(false);
    }

    /// <summary>
    ///     A refused create releases the hold, so Escape closes the dialog again afterwards.
    ///     Red: no release on a refused create; Escape stays blocked and the dialog stays open.
    /// </summary>
    [Test]
    [Description("Escape closes the create dialog after a refused create")]
    public async Task Escape_closes_the_create_dialog_after_a_refused_create()
    {
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);
        await _tasks.FillNewTaskAsync(TestProject, new string('z', 501), "Too long a title").ConfigureAwait(false);
        await _tasks.CreateButton.ClickAsync().ConfigureAwait(false);
        await Expect(_tasks.DialogAlert).ToContainTextAsync("Title cannot exceed 500 characters").ConfigureAwait(false);
        await WaitUntilEscapeWouldCloseAsync().ConfigureAwait(false);

        await Page.Keyboard.PressAsync("Escape").ConfigureAwait(false);

        await Expect(_tasks.Dialog).ToBeHiddenAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     A dialog that goes away mid-submit, here because the page navigates while the create is in flight, leaves no
    ///     hold behind: back on Tasks, Escape closes a new dialog.
    ///     Red: DialogHold.DisposeAsync does not release a hold; Escape stays blocked on the new dialog.
    /// </summary>
    [Test]
    [Description("A dialog closed mid-submit leaves no stuck hold")]
    public async Task A_dialog_closed_mid_submit_leaves_no_stuck_hold()
    {
        await Page.RouteAsync("**/api/tasks", async route =>
        {
            if (string.Equals(route.Request.Method, "POST", StringComparison.Ordinal))
            {
                await Task.Delay(3000).ConfigureAwait(false);
            }

            await route.ContinueAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);
        await _tasks.FillNewTaskAsync(TestProject, Unique("Navigated away"), "Left mid-submit").ConfigureAwait(false);
        await _tasks.CreateButton.ClickAsync().ConfigureAwait(false);

        await Page.EvaluateAsync("() => Blazor.navigateTo('/runs')").ConfigureAwait(false);
        await Expect(_tasks.Dialog).ToBeHiddenAsync().ConfigureAwait(false);
        await Page.WaitForTimeoutAsync(3500).ConfigureAwait(false);
        await Page.EvaluateAsync("() => Blazor.navigateTo('/tasks')").ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);
        await WaitUntilEscapeWouldCloseAsync().ConfigureAwait(false);

        await Page.Keyboard.PressAsync("Escape").ConfigureAwait(false);

        await Expect(_tasks.Dialog).ToBeHiddenAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Created shows the creation time in the viewer's time zone, on Tasks and on Projects, for both the project and
    ///     its tasks. The browser runs in Pacific/Auckland, so a UTC time differs by hours.
    ///     Red: Tasks renders <c>CreatedAt</c> with <c>FormatString="{0:g}"</c>, as before; the row shows UTC.
    ///     Red: Projects renders the project's <c>CreatedAt</c> with <c>{0:g}</c>; its row shows UTC.
    ///     Red: Projects renders a task's <c>CreatedAt</c> with <c>{0:g}</c>; the task row shows UTC.
    /// </summary>
    [Test]
    [Description("Created shows the viewer's local time")]
    public async Task Created_shows_the_viewers_local_time()
    {
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var tasks = await GetApiAsync<JsonElement>("/api/tasks?page=1&pageSize=200").ConfigureAwait(false);
        var task = tasks.GetProperty("items").EnumerateArray().First(t => string.Equals(t.GetProperty("title").GetString(), "Fix bug B", StringComparison.Ordinal));
        var projects = await GetApiAsync<JsonElement>("/api/projects?page=1&pageSize=200").ConfigureAwait(false);
        var project = projects.GetProperty("items").EnumerateArray().First(p => string.Equals(p.GetProperty("projectName").GetString(), TestProject, StringComparison.Ordinal));
        string Local(JsonElement e) => TimeZoneInfo.ConvertTimeFromUtc(e.GetProperty("createdAt").GetDateTime().ToUniversalTime(), auckland)
            .ToString("g", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            TimezoneId = "Pacific/Auckland",
            Locale = "en-US",
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var board = new TasksPage(page, BaseUrl);
        await board.NavigateAsync().ConfigureAwait(false);
        await Expect(board.GetRowByText("Fix bug B")).ToContainTextAsync(Local(task)).ConfigureAwait(false);

        var projectsPage = new ProjectsPage(page, BaseUrl);
        await projectsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(projectsPage.GetRowByText(TestProject)).ToContainTextAsync(Local(project)).ConfigureAwait(false);
        await projectsPage.GetRowByText(TestProject).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = TestProject })
            .ClickAsync().ConfigureAwait(false);
        await Expect(projectsPage.SelectedProjectPanel.Locator("tr", new LocatorLocatorOptions { HasText = "Fix bug B" }))
            .ToContainTextAsync(Local(task)).ConfigureAwait(false);
    }

    /// <summary>Waits until Radzen would close the dialog on Escape: armed 500ms after opening, and no popup showing.</summary>
    private async Task WaitUntilEscapeWouldCloseAsync() =>
        await Page.WaitForFunctionAsync(
                "() => document.querySelector('.rz-dialog-content').options !== undefined"
                + " && Array.from(document.querySelectorAll('.rz-popup,.rz-overlaypanel')).every(p => p.style.display === 'none')")
            .ConfigureAwait(false);

    /// <summary>
    ///     The dialog opens with focus on the Project picker.
    ///     Red: no focus call after the first render; focus never reaches the picker and the wait times out.
    /// </summary>
    [Test]
    [Description("The create dialog opens with focus on project")]
    public async Task The_create_dialog_opens_with_focus_on_project()
    {
        await _tasks.NavigateAsync().ConfigureAwait(false);
        await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);

        var focused = await Page.WaitForFunctionAsync(
                "() => document.activeElement && document.activeElement.closest(\"[data-testid='task-project']\") !== null",
                null, new PageWaitForFunctionOptions { Timeout = 5000 })
            .ConfigureAwait(false);
        (await focused.JsonValueAsync<bool>().ConfigureAwait(false)).Should().BeTrue();
    }

    /// <summary>
    ///     Every field in the create and the edit dialog is named for a screen reader by its visible label, so a role query
    ///     by that label finds exactly one field. The drop-downs are Radzen's, whose input Radzen names after the value.
    ///     Red: <c>Component</c> dropped from Title's form field; the label points at nothing and no textbox is named Title.
    ///     Red: <c>Component</c> dropped from Description's form field; no textbox is named Description.
    ///     Red: <c>Component</c> dropped from Prompt's form field; no textbox is named Prompt.
    ///     Red: the Project picker's <c>InputAttributes</c> removed; it is named "Empty", not Project.
    ///     Red: the Priority picker's <c>InputAttributes</c> removed; it is named "1", not Priority.
    ///     Red: the Complexity picker's <c>InputAttributes</c> removed; it is named "1", not Complexity.
    ///     Each goes red in both dialogs: they share <c>TaskFields</c>.
    /// </summary>
    [TestCase("create")]
    [TestCase("edit")]
    [Description("Every task dialog field is named by its label")]
    public async Task Every_task_dialog_field_is_named_by_its_label(string dialog)
    {
        await _tasks.NavigateAsync().ConfigureAwait(false);
        if (string.Equals(dialog, "create", StringComparison.Ordinal))
        {
            await _tasks.OpenCreateDialogAsync().ConfigureAwait(false);
        }
        else
        {
            await _tasks.GetEditButton("Fix bug B").ClickAsync().ConfigureAwait(false);
            await Expect(_tasks.TitleInput).ToBeVisibleAsync().ConfigureAwait(false);
        }

        await _tasks.Dialog.GetByText("Advanced", new LocatorGetByTextOptions { Exact = true }).ClickAsync().ConfigureAwait(false);

        var unnamed = new List<string>();
        foreach (var label in new[] { "Project", "Title", "Description", "Priority", "Complexity", "Prompt" })
        {
            var count = await _tasks.Dialog
                .GetByRole(AriaRole.Textbox, new LocatorGetByRoleOptions { Name = label, Exact = true })
                .CountAsync().ConfigureAwait(false);
            if (count != 1)
            {
                unnamed.Add($"{label} ({count} found)");
            }
        }

        string.Join("; ", unnamed).Should().BeEmpty($"every field in the {dialog} dialog is named by its label:\n"
            + await _tasks.Dialog.AriaSnapshotAsync().ConfigureAwait(false));
    }
}
