using System.Text.RegularExpressions;
using Daedalus.Tests.Playwright.Browser.Fixtures;
using Daedalus.Tests.Playwright.Browser.PageObjects;
using Daedalus.Tests.Playwright.Browser.Utilities;
using Thalos.Workflow;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     Phase 2.13, #351: Home for a signed-in user is what needs the person, in four cards in order of urgency, with one
///     New task button. The fixture seeds a run parked at the human gate, a running run, a failed run and a succeeded run
///     with a pull request, so each card has a row of its own; a test that needs a card empty or failing answers that
///     card's list itself.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Home")]
[Description("Home: what needs the person signed in")]
public class HomePageBrowserTests : BrowserTestBase
{
    private const string EmptyList = """{"runs":[],"next":null}""";

    /// <summary>How long a live change may take to reach the page.</summary>
    private static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(10);

    private HomePage _home = null!;

    private static RunSeeds Runs => E2EServerFixture.Runs;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted)
            return;
        _home = new HomePage(Page, BaseUrl);
    }

    /// <summary>Answers the run list whose query contains <paramref name="filter"/>, such as <c>status=Failed</c>, with <paramref name="fulfil"/>.</summary>
    private Task RouteRunListAsync(string filter, Func<IRoute, Task> fulfil) =>
        Page.RouteAsync(
            url => url.Contains("/api/workflow-runs?", StringComparison.Ordinal) && url.Contains(filter, StringComparison.Ordinal),
            fulfil);

    /// <summary>A page of one run with no pull request, and a cursor saying the list has more.</summary>
    private static Task OneRunAndMoreAsync(IRoute route) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new
            {
                runs = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(), process = "manufacture", processVersion = 1, status = "Running", currentNode = "implement",
                        awaitingSignal = (string?)null, startedBy = "e2e", createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow,
                        taskId = (Guid?)null, taskCode = (string?)null, taskTitle = "A faked run", startedByName = (string?)null,
                        failureReason = (string?)null, prUrl = (string?)null,
                    },
                },
                next = "more",
            }),
        });

    private static Task EmptyAsync(IRoute route) =>
        route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = EmptyList });

    /// <summary>
    ///     Phase 2.14, owner-approved on 2026-10-16: Home's cards follow the runs live. A running run that reaches the
    ///     human gate moves from Running to Awaiting approval without a reload. The change is a real commit through the
    ///     store, which the broadcaster sends over the hub; the test waits for the hub's handshake before making it, so
    ///     the change cannot be sent before the browser listens.
    ///     Red: drop the HomeEffects.OnRunChanged call from RunHubConnection.OnChangedAsync; the gate never shows.
    ///     Red: HomeState.Live without Running; the run stays under Running after it parked.
    ///     Red: the change re-fetches as a foreground read; the awaiting card turns into its skeleton, so its row count
    ///     drops to zero while the re-fetch is in flight, which the row-count watch sees.
    ///     Red: reload the page on a change; the no-reload marker is gone.
    /// </summary>
    [Test]
    [Description("Home updates when a run changes")]
    public async Task Home_updates_when_a_run_changes()
    {
        var live = await Runs.StartAsync($"e2e-home-live-{Guid.NewGuid():N}", CancellationToken.None).ConfigureAwait(false);
        var handshaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Page.WebSocket += (_, socket) =>
        {
            if (socket.Url.Contains("/hubs/workflow-runs", StringComparison.Ordinal))
            {
                socket.FrameReceived += (_, _) => handshaken.TrySetResult();
            }
        };

        try
        {
            await _home.NavigateAsync().ConfigureAwait(false);
            var name = live.ToString()[..8];
            await Expect(_home.Row(HomePage.Running, name)).ToBeVisibleAsync().ConfigureAwait(false);
            await Expect(_home.Row(HomePage.Awaiting, RunSeeds.AwaitingTaskTitle)).ToBeVisibleAsync().ConfigureAwait(false);
            await handshaken.Task.WaitAsync(LiveTimeout).ConfigureAwait(false);
            await Page.EvaluateAsync(
                """
                () => {
                    window.__noReload = true;
                    window.__awaitingEmptied = false;
                    const card = document.querySelector("[data-testid='needs-me-awaiting']");
                    new MutationObserver(() => {
                        if (card.querySelectorAll("[data-testid='needs-me-row']").length === 0) window.__awaitingEmptied = true;
                    }).observe(card, { childList: true, subtree: true });
                }
                """).ConfigureAwait(false);

            await Runs.CompleteAsync(
                    live, new WorkflowTransition("gate", WorkflowStatus.Awaiting, RunSeeds.HumanApproval, WorkflowEventKind.Awaiting),
                    CancellationToken.None)
                .ConfigureAwait(false);

            var within = new LocatorAssertionsToBeVisibleOptions { Timeout = (float)LiveTimeout.TotalMilliseconds };
            await Expect(_home.Row(HomePage.Awaiting, name)).ToBeVisibleAsync(within).ConfigureAwait(false);
            await Expect(_home.Row(HomePage.Running, name)).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions
            {
                Timeout = (float)LiveTimeout.TotalMilliseconds,
            }).ConfigureAwait(false);
            (await Page.EvaluateAsync<bool>("() => window.__noReload === true").ConfigureAwait(false))
                .Should().BeTrue("the change arrived without a reload");
            (await Page.EvaluateAsync<bool>("() => window.__awaitingEmptied").ConfigureAwait(false))
                .Should().BeFalse("a live re-fetch keeps the rows on show, never the skeleton");
        }
        finally
        {
            await Runs.Store.CancelAsync(live, "The live Home scenario is done with it.", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The run parked at the human gate is listed under Awaiting approval with its task's title, how long it has
    ///     waited, and an Open gate link to the run. The heading carries the count.
    ///     Red: the awaiting card queries status=Running; the gate's task is not in it.
    ///     Red: the row leaves out the time it has waited; "waiting" is missing.
    ///     Red: Open gate links to <c>runs</c> without the id; the href is not the run's.
    ///     Red: the heading has no count; the heading does not read "Awaiting approval (n)".
    ///     Red: the heading always appends "+"; a complete list's exact count reads "(n+)".
    /// </summary>
    [Test]
    [Description("Home lists a run awaiting approval with an Open gate link")]
    public async Task Home_lists_a_run_awaiting_approval_with_an_open_gate_link()
    {
        await _home.NavigateAsync().ConfigureAwait(false);
        var row = _home.Row(HomePage.Awaiting, RunSeeds.AwaitingTaskTitle);

        await Expect(row).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(row).ToContainTextAsync(new Regex(@"waiting (less than a minute|\d+ (min|h|d))")).ConfigureAwait(false);
        var gate = row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Open gate" });
        await Expect(gate).ToHaveAttributeAsync("href", $"runs/{Runs.AwaitingApproval}").ConfigureAwait(false);
        await Expect(_home.CardHeading(HomePage.Awaiting)).ToHaveTextAsync(new Regex(@"^\s*Awaiting approval \(\d+\)\s*$"))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     The seeded failed run, updated today, is listed under Failed in the last 7 days with its reason as text, and
    ///     links to the run. It belongs to no task, so the row names the run instead.
    ///     Red: the failed card leaves out failureReason; the reason is missing.
    ///     Red: the row's link points at <c>runs</c>; its href is not the run's.
    /// </summary>
    [Test]
    [Description("Home shows a failed run with its reason")]
    public async Task Home_shows_a_failed_run_with_its_reason()
    {
        await _home.NavigateAsync().ConfigureAwait(false);
        var row = _home.Row(HomePage.Failed, Runs.Failed.ToString()[..8]);

        await Expect(row).ToContainTextAsync("The publish step failed.").ConfigureAwait(false);
        await Expect(row.GetByRole(AriaRole.Link)).ToHaveAttributeAsync("href", $"runs/{Runs.Failed}").ConfigureAwait(false);
    }

    /// <summary>
    ///     Home has one button, New task, which is primary and opens the create dialog. There are no other buttons, so none
    ///     of the old coloured quick actions; no heading carries an icon; and nothing on Home paints a gradient.
    ///     Red: the title kept as "Dashboard"; the H4 does not read "Home".
    ///     Red: the old Generate PRD quick action added back, Info; Home has two buttons.
    ///     Red: New task given ButtonStyle.Success; it lacks the primary class.
    ///     Red: the button navigates to Tasks instead of opening the dialog; the dialog's project picker is missing.
    ///     Red: an icon added beside the Running heading; a heading holds an icon.
    ///     Red: a card given a linear-gradient background; a gradient is found.
    /// </summary>
    [Test]
    [Description("Home shows one New task button and no coloured quick actions")]
    public async Task Home_shows_one_new_task_button_and_no_coloured_quick_actions()
    {
        await _home.NavigateAsync().ConfigureAwait(false);
        await Expect(_home.Row(HomePage.Running, RunSeeds.LinkedTaskTitle)).ToBeVisibleAsync().ConfigureAwait(false);

        await Expect(_home.Title.Locator("h4")).ToHaveTextAsync("Home").ConfigureAwait(false);
        await Expect(_home.Panel.GetByRole(AriaRole.Button)).ToHaveCountAsync(1).ConfigureAwait(false);
        await Expect(_home.NewTaskButton).ToHaveClassAsync(new Regex(@"\brz-primary\b")).ConfigureAwait(false);
        await Expect(_home.Panel.Locator("h4 .rzi, h6 .rzi, h4 .rz-icon, h6 .rz-icon")).ToHaveCountAsync(0).ConfigureAwait(false);
        var gradients = await _home.Panel.EvaluateAsync<int>(
                "panel => [panel, ...panel.querySelectorAll('*')].filter(e => getComputedStyle(e).backgroundImage.includes('gradient')).length")
            .ConfigureAwait(false);
        gradients.Should().Be(0, "Home paints no gradient");

        await _home.NewTaskButton.ClickAsync().ConfigureAwait(false);
        await Expect(new TasksPage(Page, BaseUrl).ProjectPicker).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     A card with nothing in it says what is empty in one muted line, and its count is zero. Every list is answered
    ///     empty by the test.
    ///     Red: the card renders nothing when it has no rows; the awaiting card's line is missing.
    ///     Red: the running card's empty text is the awaiting card's; it does not say nothing is running.
    ///     Red: the failed card's empty text says "No runs."; it does not name the last 7 days.
    ///     Red: the pull requests card's empty text is left out; its line is missing.
    ///     Red: the pull requests card always says "in the latest 20 runs"; a complete empty list does not read "yet".
    ///     Red: the count shows only when there are rows; the heading is not "Awaiting approval (0)".
    /// </summary>
    [Test]
    [Description("An empty card says what is empty")]
    public async Task An_empty_card_says_what_is_empty()
    {
        await Page.RouteAsync(url => url.Contains("/api/workflow-runs?", StringComparison.Ordinal), EmptyAsync).ConfigureAwait(false);
        await _home.NavigateAsync().ConfigureAwait(false);

        await Expect(_home.Empty(HomePage.Awaiting)).ToHaveTextAsync("Nothing waiting for approval.").ConfigureAwait(false);
        await Expect(_home.Empty(HomePage.Running)).ToHaveTextAsync("Nothing running.").ConfigureAwait(false);
        await Expect(_home.Empty(HomePage.Failed)).ToHaveTextAsync("No failed runs in the last 7 days.").ConfigureAwait(false);
        await Expect(_home.Empty(HomePage.PullRequests)).ToHaveTextAsync("No pull requests yet.").ConfigureAwait(false);
        await Expect(_home.CardHeading(HomePage.Awaiting)).ToHaveTextAsync(new Regex(@"^\s*Awaiting approval \(0\)\s*$")).ConfigureAwait(false);
    }

    /// <summary>
    ///     A card whose list cannot be read says why in a Danger alert with Retry, and only that card fails: the others
    ///     still list their runs, where the old dashboard showed a silent 0. Retry reads the list again. The failed list is
    ///     refused by the test once, then answered by the server.
    ///     Red: the failure shows as an empty card; the alert is missing.
    ///     Red: one failure fails the whole panel; the awaiting card's row is missing.
    ///     Red: Retry does nothing; the reason never shows.
    /// </summary>
    [Test]
    [Description("A card that cannot load says why and retries on its own")]
    public async Task A_card_that_cannot_load_says_why_and_retries_on_its_own()
    {
        var refused = false;
        await RouteRunListAsync("status=Failed", async route =>
        {
            if (!refused)
            {
                refused = true;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 500,
                    ContentType = "application/problem+json",
                    Body = """{"detail":"The run store is down."}""",
                }).ConfigureAwait(false);
                return;
            }

            await route.ContinueAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        await _home.NavigateAsync().ConfigureAwait(false);

        var error = _home.Error(HomePage.Failed);
        await Expect(error).ToContainTextAsync("The run store is down.").ConfigureAwait(false);
        await Expect(_home.Row(HomePage.Awaiting, RunSeeds.AwaitingTaskTitle)).ToBeVisibleAsync().ConfigureAwait(false);

        await error.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retry" }).ClickAsync().ConfigureAwait(false);
        await Expect(_home.Row(HomePage.Failed, Runs.Failed.ToString()[..8])).ToContainTextAsync("The publish step failed.")
            .ConfigureAwait(false);
        await Expect(error).ToHaveCountAsync(0).ConfigureAwait(false);
    }

    /// <summary>
    ///     A list that says it has more than the page Home read is shown as incomplete: the count reads "n+", and a pull
    ///     requests card that found none says only the latest runs had none, not that there are none. The running and the
    ///     pull requests lists are answered by the test with one run and a cursor.
    ///     Red: the heading ignores More; it reads "Running (1)".
    ///     Red: the pull requests card always says "No pull requests yet."; the incomplete line is missing.
    /// </summary>
    [Test]
    [Description("An incomplete list says so")]
    public async Task An_incomplete_list_says_so()
    {
        await RouteRunListAsync("status=Running", OneRunAndMoreAsync).ConfigureAwait(false);
        await RouteRunListAsync("process=manufacture", OneRunAndMoreAsync).ConfigureAwait(false);
        await _home.NavigateAsync().ConfigureAwait(false);

        await Expect(_home.CardHeading(HomePage.Running)).ToHaveTextAsync(new Regex(@"^\s*Running \(1\+\)\s*$")).ConfigureAwait(false);
        await Expect(_home.Empty(HomePage.PullRequests)).ToHaveTextAsync("No pull requests in the latest 20 runs.").ConfigureAwait(false);
    }

    /// <summary>
    ///     The running run is listed with its task's title and the node it is at, linking to the run.
    ///     Red: the row leaves out the current node; "implement" is missing.
    ///     Red: the row's link points at Tasks; its href is not the run's.
    /// </summary>
    [Test]
    [Description("Home lists a running run with its current node")]
    public async Task Home_lists_a_running_run_with_its_current_node()
    {
        await _home.NavigateAsync().ConfigureAwait(false);
        var row = _home.Row(HomePage.Running, RunSeeds.LinkedTaskTitle);

        await Expect(row).ToContainTextAsync("implement").ConfigureAwait(false);
        await Expect(row.GetByRole(AriaRole.Link)).ToHaveAttributeAsync("href", $"runs/{Runs.Running}").ConfigureAwait(false);
    }

    /// <summary>
    ///     The succeeded run's pull request is listed with its task's title and a link to the pull request itself.
    ///     Red: the link points at the run instead of the pull request; the href is not the PR's.
    ///     Red: the link text is the whole URL; it is not "#42".
    /// </summary>
    [Test]
    [Description("Home lists a recent pull request with a link to it")]
    public async Task Home_lists_a_recent_pull_request_with_a_link_to_it()
    {
        await _home.NavigateAsync().ConfigureAwait(false);
        var link = _home.Row(HomePage.PullRequests, RunSeeds.PullRequestTaskTitle).GetByRole(AriaRole.Link);

        await Expect(link).ToHaveAttributeAsync("href", RunSeeds.PullRequestUrl).ConfigureAwait(false);
        await Expect(link).ToHaveTextAsync("#42").ConfigureAwait(false);
    }

    /// <summary>
    ///     Create and manufacture from Home, for a project with no repository URL, creates the task but the manufacture is
    ///     refused for real; the dialog closes and Home says why in an alert.
    ///     Red: the dialog stays open when the manufacture is refused; it is not hidden.
    ///     Red: Home ignores the dialog's result; the alert is missing.
    ///     Red: the alert is set only for a signing-in reason; it is missing.
    /// </summary>
    [Test]
    [Description("A refused manufacture from Home says why")]
    public async Task A_refused_manufacture_from_home_says_why()
    {
        var tasks = new TasksPage(Page, BaseUrl);
        await _home.NavigateAsync().ConfigureAwait(false);
        await _home.NewTaskButton.ClickAsync().ConfigureAwait(false);
        await Expect(tasks.ProjectPicker).ToBeVisibleAsync().ConfigureAwait(false);
        await Page.WaitForFunctionAsync(
                "() => { const d = document.querySelector('.rz-dialog'); return d !== null && d.getAnimations({ subtree: true }).length === 0; }")
            .ConfigureAwait(false);
        await tasks.FillNewTaskAsync("Test Project", $"Home task {Guid.NewGuid().ToString("N")[..8]}", "Refused at manufacture")
            .ConfigureAwait(false);

        await tasks.CreateAndManufactureButton.ClickAsync().ConfigureAwait(false);

        await Expect(tasks.Dialog).ToBeHiddenAsync().ConfigureAwait(false);
        await Expect(_home.ManufactureRefusedAlert).ToContainTextAsync("no repository URL").ConfigureAwait(false);
    }

    /// <summary>
    ///     The run list needs the developer or admin role, so for anyone else Home reads none of it: one muted line says
    ///     who sees runs, in place of the four cards, with no alert. New task stays, and its dialog offers Create but not
    ///     Create and manufacture, which the manufacture endpoint would refuse with the same rule.
    ///     Red: the panel reads the four lists whatever the role; a run list request is made.
    ///     Red: the line's text left out; it is not the sentence.
    ///     Red: a Danger alert for a refused card added under the line; an alert showed on Home.
    ///     Red: Create and manufacture shown to everyone, _canStart = true; the dialog holds it.
    /// </summary>
    [TestCase("task-manager")]
    [TestCase("project-manager")]
    [TestCase("analyst")]
    [Description("Home for someone who may not see runs reads none and says so")]
    public async Task Home_for_someone_who_may_not_see_runs_reads_none_and_says_so(string role)
    {
        await TestRoles.WriteAsync(Context, BaseUrl, role).ConfigureAwait(false);
        await TestAuth.WatchForAsync(Page, ".rz-alert").ConfigureAwait(false);
        var runReads = new List<string>();
        Page.Request += (_, request) =>
        {
            if (request.Url.Contains("/api/workflow-runs", StringComparison.Ordinal))
            {
                runReads.Add(request.Url);
            }
        };
        await _home.NavigateAsync().ConfigureAwait(false);

        await Expect(_home.NoRunsLine).ToHaveTextAsync("Runs are visible to developers and admins.").ConfigureAwait(false);
        await Expect(_home.Card(HomePage.Awaiting)).ToHaveCountAsync(0).ConfigureAwait(false);
        var tasks = new TasksPage(Page, BaseUrl);
        await _home.NewTaskButton.ClickAsync().ConfigureAwait(false);
        await Expect(tasks.CreateButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(tasks.CreateAndManufactureButton).ToHaveCountAsync(0).ConfigureAwait(false);

        runReads.Should().BeEmpty($"a {role} may not read the run list");
        (await TestAuth.PathsThatShowedAsync(Page, ".rz-alert").ConfigureAwait(false))
            .Should().BeEmpty($"Home shows a {role} no alert");
    }

    /// <summary>
    ///     A developer may start a run, so the dialog from Home offers Create and manufacture, and Home shows the cards,
    ///     not the line.
    ///     Red: Create and manufacture hidden from everyone, _canStart = false; the dialog lacks it.
    ///     Red: the line rendered outside the role check; it is there for a developer.
    /// </summary>
    [Test]
    [Description("Home for a developer shows the cards and offers Create and manufacture")]
    public async Task Home_for_a_developer_shows_the_cards_and_offers_create_and_manufacture()
    {
        await TestRoles.WriteAsync(Context, BaseUrl, "developer").ConfigureAwait(false);
        await _home.NavigateAsync().ConfigureAwait(false);

        await Expect(_home.Row(HomePage.Running, RunSeeds.LinkedTaskTitle)).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_home.NoRunsLine).ToHaveCountAsync(0).ConfigureAwait(false);
        var tasks = new TasksPage(Page, BaseUrl);
        await _home.NewTaskButton.ClickAsync().ConfigureAwait(false);
        await Expect(tasks.CreateAndManufactureButton).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     At desktop width the cards sit two by two, awaiting beside running; at tablet width they stack in one column in
    ///     order of urgency: awaiting approval, running, failed, pull requests.
    ///     Red: the columns take SizeMD="6"; at 1024 running sits beside awaiting.
    ///     Red: the columns are Size="12" only; at 1280 running is below awaiting.
    ///     Red: the running column given Order="3" OrderLG="0"; at 1024 running is below failed.
    /// </summary>
    [Test]
    [Description("The cards sit two by two on a desktop and stack in urgency order on a tablet")]
    public async Task The_cards_sit_two_by_two_on_a_desktop_and_stack_on_a_tablet()
    {
        await Page.SetViewportSizeAsync(1280, 900).ConfigureAwait(false);
        await _home.NavigateAsync().ConfigureAwait(false);
        await Expect(_home.Row(HomePage.Running, RunSeeds.LinkedTaskTitle)).ToBeVisibleAsync().ConfigureAwait(false);
        var desktop = await SettledCardBoxesAsync(sidebarExpanded: true).ConfigureAwait(false);
        desktop[1][1].Should().BeApproximately(desktop[0][1], 1, "on a desktop running sits beside awaiting");

        await Page.SetViewportSizeAsync(1024, 900).ConfigureAwait(false);
        var tablet = await SettledCardBoxesAsync(sidebarExpanded: false).ConfigureAwait(false);
        for (var i = 1; i < tablet.Length; i++)
        {
            tablet[i][0].Should().BeApproximately(tablet[0][0], 1, "on a tablet the cards share one column");
            tablet[i][1].Should().BeGreaterThan(tablet[i - 1][1], "on a tablet the cards stack in order of urgency");
        }
    }

    /// <summary>
    ///     The four cards' left and top, awaiting, running, failed and pull requests, once the layout has settled for the
    ///     width: the sidebar carries the class the width gives it, and no CSS transition, such as the sidebar's own,
    ///     still moves the content column. All four are read in one evaluation, so in the same frame.
    /// </summary>
    private async Task<double[][]> SettledCardBoxesAsync(bool sidebarExpanded)
    {
        var sidebarClass = sidebarExpanded ? "rz-sidebar-expanded" : "rz-sidebar-collapsed";
        await Page.WaitForFunctionAsync(
                "cls => { const sidebar = document.querySelector('.rz-sidebar');"
                + " return sidebar !== null && sidebar.classList.contains(cls)"
                + " && document.getAnimations().every(a => !(a instanceof CSSTransition)); }",
                sidebarClass)
            .ConfigureAwait(false);
        return await Page.EvaluateAsync<double[][]>(
                "ids => ids.map(id => { const r = document.querySelector(`[data-testid='${id}']`).getBoundingClientRect();"
                + " return [r.left, r.top]; })",
                new[] { HomePage.Awaiting, HomePage.Running, HomePage.Failed, HomePage.PullRequests })
            .ConfigureAwait(false);
    }

    [Test]
    [Description("Bug #1 regression: favicon.svg should load successfully (was favicon.png causing 404)")]
    public async Task Favicon_ShouldLoad_Without404()
    {
        await _home.NavigateAsync().ConfigureAwait(false);

        var faviconResponse = await Page.APIRequest.GetAsync(new Uri(BaseUrl, "/favicon.svg").ToString())
            .ConfigureAwait(false);
        faviconResponse.Status.Should().Be(200, "favicon.svg should be served successfully");
    }

    [Test]
    [Description("Bug #1 regression: index.html should reference favicon.svg, not favicon.png")]
    public async Task IndexHtml_ShouldReference_FaviconSvg()
    {
        var response = await Page.APIRequest.GetAsync(BaseUrl.ToString()).ConfigureAwait(false);
        var body = await response.TextAsync().ConfigureAwait(false);

        body.Should().Contain("favicon.svg", "index.html should reference the SVG favicon");
        body.Should().NotContain("favicon.png", "index.html should not reference the old PNG favicon");
    }
}
