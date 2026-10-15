using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Daedalus.Tests.Playwright.Browser.Fixtures;
using Daedalus.Tests.Playwright.Browser.PageObjects;
using Daedalus.Tests.Playwright.Browser.Utilities;
using Thalos.Workflow;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     The run console against runs in the real workflow store (<see cref="RunSeeds"/>). The four seeded runs are never
///     changed: a scenario that changes a run seeds its own, and the writes the gate makes are answered by routes the
///     test fulfils, so the scenarios may run in any order.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Runs")]
[Description("Run console browser tests: the list, the run detail and its gate, the live update and the Tasks link")]
public sealed class RunsConsoleBrowserTests : BrowserTestBase
{
    /// <summary>The seeded gate run's timeline: its start, the implement completion and the review completion.</summary>
    private const int GateRunSteps = 3;

    private static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(10);

    private RunsPage _runsPage = null!;
    private RunDetailPage _runPage = null!;

    private static RunSeeds Runs => E2EServerFixture.Runs;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted)
            return;
        _runsPage = new RunsPage(Page, BaseUrl);
        _runPage = new RunDetailPage(Page, BaseUrl);
    }

    /// <summary>
    ///     The four seeded runs are listed newest first, each badge with its status text. Other scenarios' own runs may
    ///     be listed too, so the order is checked among the four.
    ///     Red: list the API's page oldest first by reversing <c>page.Runs</c> in <c>WorkflowRunsController.List</c>; the
    ///     order assertion fails. The seeded order is the store's <c>ORDER BY created_at</c>, in Thalos.NET, so the
    ///     reversal stands in for the brief's <c>ORDER BY created_at ASC</c>.
    ///     Red: drop <c>RunStatusBadge</c> from <c>RunsGrid</c>'s Status template; the Running badge is missing.
    ///     Red: render <c>run.Status</c> raw in <c>RunStatusBadge</c>; the gate run reads "Awaiting".
    /// </summary>
    [Test]
    [Description("The list shows the four seeded runs newest first, each with its status text")]
    public async Task Runs_ListsSeededRunsNewestFirst()
    {
        await _runsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_runsPage.RowFor(Runs.Failed)).ToBeVisibleAsync().ConfigureAwait(false);

        var hrefs = await _runsPage.RowLinks.EvaluateAllAsync<string[]>("links => links.map(a => a.getAttribute('href'))")
            .ConfigureAwait(false);
        int[] positions =
        [
            .. new[] { Runs.Running, Runs.AwaitingApproval, Runs.Succeeded, Runs.Failed }.Select(id => Array.IndexOf(hrefs, $"runs/{id}")),
        ];
        positions.Should().NotContain(-1, "every seeded run is listed").And.BeInAscendingOrder("runs are listed newest first");

        await Expect(_runsPage.RowFor(Runs.Running).Locator("[data-testid='run-status']")).ToHaveTextAsync("Running")
            .ConfigureAwait(false);
        await Expect(_runsPage.RowFor(Runs.AwaitingApproval).Locator("[data-testid='run-status']"))
            .ToHaveTextAsync("Awaiting approval").ConfigureAwait(false);
    }

    /// <summary>
    ///     Choosing Awaiting leaves the one run at the gate.
    ///     Red: <c>Runs.razor</c>'s <c>FilterAsync</c> ignores the status and passes <see langword="null"/>; every run is
    ///     listed.
    ///     Red: <c>FilterAsync</c> passes "Succeeded" whatever was chosen; one row is listed, the succeeded run.
    /// </summary>
    [Test]
    [Description("Filtering by Awaiting leaves only the run at the gate")]
    public async Task Runs_FilterByAwaiting_LeavesTheGateRun()
    {
        await _runsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_runsPage.RowFor(Runs.Failed)).ToBeVisibleAsync().ConfigureAwait(false);

        await _runsPage.FilterByStatusAsync("Awaiting").ConfigureAwait(false);

        await Expect(_runsPage.Rows).ToHaveCountAsync(1).ConfigureAwait(false);
        await Expect(_runsPage.RowFor(Runs.AwaitingApproval)).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     The menu counts runs awaiting approval, not every parked run: this test parks a run of its own at another
    ///     signal, and cancels it after.
    ///     Red: <c>RunsEffects.LoadAwaitingApprovalAsync</c> counts every Awaiting row; the count reads 2.
    /// </summary>
    [Test]
    [Description("The menu shows one run awaiting approval")]
    public async Task Menu_ShowsTheAwaitingApprovalCount()
    {
        var other = await Runs.StartAsync($"e2e-other-signal-{Guid.NewGuid():N}", CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Runs.CompleteAsync(
                other, new WorkflowTransition("ci", WorkflowStatus.Awaiting, "ci_green", WorkflowEventKind.Awaiting), CancellationToken.None)
                .ConfigureAwait(false);

            await _runsPage.NavigateAsync().ConfigureAwait(false);

            await Expect(_runsPage.AwaitingCount).ToHaveTextAsync("1").ConfigureAwait(false);
        }
        finally
        {
            await Runs.Store.CancelAsync(other, "The menu scenario is done with it.", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The run at the gate shows its header, the gate with both deferred findings, the four tabs and its timeline; a
    ///     running run shows no gate.
    ///     Red: <c>RunHeader</c> shows the id's last 8 characters; the header lacks "Run " and the first 8.
    ///     Red: <c>RunsReducers.IsAtGate</c> compares the signal with "approve"; the gate panel is missing.
    ///     Red: <c>GatePanel</c> names its checkboxes <c>gate-finding-{id}</c>; mechanism-1 is missing.
    ///     Red: <c>GatePanel</c> lists only <c>Run.DeferredFindings.Take(1)</c>; mechanism-2 is missing.
    ///     Red: drop the Writes tab from <c>RunDetail.razor</c>; the tab headers differ.
    ///     Red: <c>RunTimeline</c> starts its loop at the second step; one step is missing.
    ///     Red: <c>RunsReducers.IsAtGate</c> returns true for every run; the running run shows the gate.
    /// </summary>
    [Test]
    [Description("The gate run shows its header, two deferred findings and the four tabs")]
    public async Task RunDetail_GateRun_ShowsGateAndTabs()
    {
        await _runPage.NavigateAsync(Runs.AwaitingApproval).ConfigureAwait(false);

        await Expect(_runPage.Header).ToContainTextAsync($"Run {Runs.AwaitingApproval.ToString()[..8]}").ConfigureAwait(false);
        await Expect(_runPage.GatePanel).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_runPage.DropFinding("mechanism-1")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_runPage.DropFinding("mechanism-2")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_runPage.TabHeaders).ToHaveTextAsync(["Timeline", "Review", "Usage", "Writes"]).ConfigureAwait(false);
        await Expect(_runPage.Steps).ToHaveCountAsync(GateRunSteps).ConfigureAwait(false);

        await _runPage.NavigateAsync(Runs.Running).ConfigureAwait(false);
        await Expect(_runPage.Header).ToContainTextAsync($"Run {Runs.Running.ToString()[..8]}").ConfigureAwait(false);
        await Expect(_runPage.GatePanel).ToHaveCountAsync(0).ConfigureAwait(false);
    }

    /// <summary>
    ///     Approving with mechanism-1 dropped sends exactly the gate's signal, that id and the apply flag. The resume
    ///     route is answered by the test, so the seeded run stays at the gate.
    ///     Red: <c>ApiClient.ApproveRunAsync</c> sends the signal "approve"; the signal differs.
    ///     Red: <c>ApproveRunAsync</c> sends <c>DropFindings</c> empty; the dropped ids differ.
    ///     Red: <c>ApproveRunAsync</c> sends the negated apply flag; <c>applyStandingInstructions</c> is true.
    ///     Red: drop the "Run approved" notification from <c>RunDetail.ApproveAsync</c>; it never shows.
    /// </summary>
    [Test]
    [Description("Approve with one finding dropped sends exactly the gate signal, that id and the apply flag")]
    public async Task RunDetail_Approve_SendsTheExactBody()
    {
        string? body = null;
        await Page.RouteAsync("**/api/workflow-runs/*/resume", async route =>
        {
            body = route.Request.PostData;
            await route.FulfillAsync(new RouteFulfillOptions { Status = 204 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await _runPage.NavigateAsync(Runs.AwaitingApproval).ConfigureAwait(false);
        await _runPage.DropFinding("mechanism-1").ClickAsync().ConfigureAwait(false);
        await Expect(_runPage.DropFinding("mechanism-1")).ToHaveAttributeAsync("aria-checked", "true").ConfigureAwait(false);
        await _runPage.Approve.ClickAsync().ConfigureAwait(false);

        await Expect(_runPage.Notification.Filter(new LocatorFilterOptions { HasText = "Run approved" }))
            .ToBeVisibleAsync().ConfigureAwait(false);

        body.Should().NotBeNull("the gate posted a resume");
        using var sent = JsonDocument.Parse(body!);
        var root = sent.RootElement;
        root.GetProperty("signal").GetString().Should().Be("human_approval");
        root.GetProperty("dropFindings").EnumerateArray().Select(e => e.GetString()).Should().Equal("mechanism-1");
        root.GetProperty("applyStandingInstructions").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    ///     Cancel asks first. Keep run sends nothing; a reason and Cancel run send one cancel carrying that reason. The
    ///     cancel route is answered by the test, so the seeded run is not cancelled.
    ///     Red: <c>RunDetail.CancelAsync</c> calls <c>Effects.CancelAsync</c> before it opens the dialog; Keep run leaves
    ///     one call.
    ///     Red: <c>CancelRunDialog</c> sends the cancel twice; two calls.
    ///     Red: <c>CancelRunDialog</c> sends no reason; the body's reason is null.
    /// </summary>
    [Test]
    [Description("Cancel asks first: Keep run sends nothing; Cancel run sends the reason")]
    public async Task RunDetail_Cancel_NeedsTheConfirm()
    {
        var calls = new ConcurrentQueue<string?>();
        await Page.RouteAsync("**/api/workflow-runs/*/cancel", async route =>
        {
            calls.Enqueue(route.Request.PostData);
            await route.FulfillAsync(new RouteFulfillOptions { Status = 204 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await _runPage.NavigateAsync(Runs.AwaitingApproval).ConfigureAwait(false);
        await _runPage.Cancel.ClickAsync().ConfigureAwait(false);
        await _runPage.CancelKeep.ClickAsync().ConfigureAwait(false);
        await Expect(_runPage.CancelKeep).ToHaveCountAsync(0).ConfigureAwait(false);
        calls.Should().BeEmpty("Keep run sends nothing");

        await _runPage.Cancel.ClickAsync().ConfigureAwait(false);
        await _runPage.CancelReason.FillAsync("not needed").ConfigureAwait(false);
        await _runPage.CancelConfirm.ClickAsync().ConfigureAwait(false);
        await Expect(_runPage.Notification.Filter(new LocatorFilterOptions { HasText = "Run cancelled" }))
            .ToBeVisibleAsync().ConfigureAwait(false);

        calls.Should().ContainSingle("Cancel run sends one cancel");
        using var sent = JsonDocument.Parse(calls.Single()!);
        sent.RootElement.GetProperty("reason").GetString().Should().Be("not needed");
    }

    /// <summary>
    ///     An admin sees Retry on the failed run; a developer, in a browser context of its own, sees the run but no Retry.
    ///     Red: <c>RunHeader</c> shows Retry only for status "Succeeded"; the admin sees none.
    ///     Red: <c>RunAccess.CanView</c> admits only admin; the developer sees the role message, not the header.
    ///     Red: <c>RunDetail</c> sets <c>_canRetry</c> from <c>RunAccess.CanView</c>; the developer sees Retry.
    /// </summary>
    [Test]
    [Description("Retry is shown to an admin on the failed run and hidden from a developer")]
    public async Task RunDetail_Retry_IsAdminOnly()
    {
        await _runPage.NavigateAsync(Runs.Failed).ConfigureAwait(false);
        await Expect(_runPage.Retry).ToBeVisibleAsync().ConfigureAwait(false);

        await using var developer = await Browser.NewContextAsync().ConfigureAwait(false);
        await developer.AddInitScriptAsync("localStorage.setItem('daedalus.test-roles','developer')").ConfigureAwait(false);
        var page = await developer.NewPageAsync().ConfigureAwait(false);
        var developerRun = new RunDetailPage(page, BaseUrl);
        await developerRun.NavigateAsync(Runs.Failed).ConfigureAwait(false);

        await Expect(developerRun.Header).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(developerRun.Retry).ToHaveCountAsync(0).ConfigureAwait(false);
    }

    /// <summary>
    ///     The console follows the user's roles as they change, without a reload, as it must follow an OIDC sign-in that
    ///     completes after the layout rendered. An analyst on /runs sees the role message and no Runs item; becoming a
    ///     developer shows the item and loads the rows; becoming an analyst again hides the item and shows the message.
    ///     Red: <c>RunsMenuItem</c> computes <c>CanView</c> once in <c>OnInitializedAsync</c>; the Runs item never shows.
    ///     Red: <c>Runs</c> computes <c>CanView</c> once in <c>OnInitializedAsync</c>; the role message stays and no row
    ///     loads.
    ///     Red: <c>RunsMenuItem</c> keeps <c>_canView</c> once it is true; back as an analyst the item stays.
    /// </summary>
    [Test]
    [Description("The Runs item and the Runs page follow a role change without a reload")]
    public async Task Console_FollowsARoleChange_WithoutReload()
    {
        await using var context = await Browser.NewContextAsync().ConfigureAwait(false);
        await TestRoles.WriteAsync(context, BaseUrl, "analyst").ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var runs = new RunsPage(page, BaseUrl);
        await runs.NavigateAsync().ConfigureAwait(false);

        // The message shows only once the user is known, so the item's absence is checked after it.
        await Expect(runs.Forbidden).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(runs.MenuItem).ToHaveCountAsync(0).ConfigureAwait(false);
        await page.EvaluateAsync("() => { window.__noReload = true; }").ConfigureAwait(false);

        await TestRoles.WriteAsync(context, BaseUrl, "developer").ConfigureAwait(false);

        await Expect(runs.MenuItem).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(runs.RowFor(Runs.Failed)).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(runs.Forbidden).ToHaveCountAsync(0).ConfigureAwait(false);
        (await page.EvaluateAsync<bool>("() => window.__noReload === true").ConfigureAwait(false))
            .Should().BeTrue("the roles changed without a reload");

        await TestRoles.WriteAsync(context, BaseUrl, "analyst").ConfigureAwait(false);

        await Expect(runs.MenuItem).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(runs.Forbidden).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     A run opened as an analyst shows the role message; becoming an admin, without a reload, loads the run and
    ///     shows Retry on the failed run.
    ///     Red: <c>RunDetail</c> computes its access once in <c>OnInitializedAsync</c>; the header never shows.
    ///     Red: <c>RunDetail</c> keeps the <c>_canRetry</c> of the first state it saw; the header shows without Retry.
    /// </summary>
    [Test]
    [Description("The run page loads the run once the user gains the role, without a reload")]
    public async Task RunDetail_FollowsARoleChange_WithoutReload()
    {
        await using var context = await Browser.NewContextAsync().ConfigureAwait(false);
        await TestRoles.WriteAsync(context, BaseUrl, "analyst").ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var run = new RunDetailPage(page, BaseUrl);
        await run.NavigateAsync(Runs.Failed).ConfigureAwait(false);
        await Expect(run.Forbidden).ToBeVisibleAsync().ConfigureAwait(false);

        await TestRoles.WriteAsync(context, BaseUrl, "admin").ConfigureAwait(false);

        await Expect(run.Header).ToContainTextAsync($"Run {Runs.Failed.ToString()[..8]}").ConfigureAwait(false);
        await Expect(run.Retry).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(run.Forbidden).ToHaveCountAsync(0).ConfigureAwait(false);
    }

    /// <summary>
    ///     A change the store commits to the open run reaches the page through the hub: the timeline gains the step and
    ///     the header the new node, with no reload. The run is this test's own.
    ///     Red: do not register the broadcaster as <c>IWorkflowRunObserver</c> in the fixture; the step never arrives.
    ///     Red: <c>RunsEffects.OnRunChangedAsync</c> skips the selected run; the step never arrives.
    ///     Red: <c>RunHeader</c> shows the first step's node instead of <c>CurrentNode</c>; it still reads "at implement".
    ///     Red: <c>RunDetail</c> reloads the page when the run's steps change; <c>window.__noReload</c> is gone.
    /// </summary>
    [Test]
    [Description("A store change to the open run appears on the timeline without a reload")]
    public async Task RunDetail_LiveUpdate_AddsTheStepWithoutReload()
    {
        var live = await Runs.StartAsync($"e2e-live-{Guid.NewGuid():N}", CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _runPage.NavigateAsync(live).ConfigureAwait(false);
            await Expect(_runPage.Header).ToContainTextAsync("at implement").ConfigureAwait(false);
            await Expect(_runPage.Steps.First).ToBeVisibleAsync().ConfigureAwait(false);
            var before = await _runPage.Steps.CountAsync().ConfigureAwait(false);
            await Page.EvaluateAsync("() => { window.__noReload = true; }").ConfigureAwait(false);

            await Runs.CompleteAsync(
                live, new WorkflowTransition("review", WorkflowStatus.Running, null, WorkflowEventKind.Completed), CancellationToken.None)
                .ConfigureAwait(false);

            var within = new LocatorAssertionsToHaveCountOptions { Timeout = (float)LiveTimeout.TotalMilliseconds };
            await Expect(_runPage.Steps).ToHaveCountAsync(before + 1, within).ConfigureAwait(false);
            await Expect(_runPage.Header).ToContainTextAsync("at review").ConfigureAwait(false);
            (await Page.EvaluateAsync<bool>("() => window.__noReload === true").ConfigureAwait(false))
                .Should().BeTrue("the change arrived without a reload");
        }
        finally
        {
            await Runs.Store.CancelAsync(live, "The live scenario is done with it.", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     An id no run has says Run not found.
    ///     Red: <c>ApiClient.GetRunAsync</c> treats 404 as an error; the page shows the error alert instead.
    /// </summary>
    [Test]
    [Description("An unknown run id says Run not found")]
    public async Task RunDetail_UnknownId_SaysNotFound()
    {
        await _runPage.NavigateAsync(Guid.NewGuid()).ConfigureAwait(false);

        await Expect(_runPage.NotFound).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     The header's pull request link wears Radzen's link style and opens in a new tab, and the header and the list
    ///     name the starter by display name, not by the principal id.
    ///     Red: <c>RunHeader</c> renders a plain anchor again; it lacks the <c>rz-link</c> class.
    ///     Red: <c>RunHeader</c> shows <c>Run.StartedBy</c>; it reads "e2e-test-user".
    ///     Red: <c>RunsGrid</c> shows <c>run.StartedBy</c>; the row reads "e2e-test-user".
    /// </summary>
    [Test]
    [Description("The pull request link is a Radzen link, and the starter is named")]
    public async Task RunDetail_PullRequestLinkAndStarter()
    {
        await _runPage.NavigateAsync(Runs.Succeeded).ConfigureAwait(false);

        var link = Page.Locator("[data-testid='run-pr-link']");
        await Expect(link).ToHaveAccessibleNameAsync("Pull request #42, opens in a new tab").ConfigureAwait(false);
        await Expect(link).ToHaveClassAsync(new Regex(@"(^|\s)rz-link(\s|$)")).ConfigureAwait(false);
        await Expect(link).ToHaveAttributeAsync("target", "_blank").ConfigureAwait(false);
        await Expect(link).ToHaveAttributeAsync("href", RunSeeds.PullRequestUrl).ConfigureAwait(false);
        await Expect(_runPage.Header).ToContainTextAsync($"started by {RunSeeds.StarterName}").ConfigureAwait(false);

        await _runsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_runsPage.RowFor(Runs.Succeeded)).ToContainTextAsync(RunSeeds.StarterName).ConfigureAwait(false);
    }

    /// <summary>
    ///     The task carrying the Running run links its status badge to that run.
    ///     Red: <c>Tasks.razor</c> renders the badge without the link; there is no link.
    ///     Red: <c>Tasks.razor</c> links to <c>runs/{task.Id}</c>; the URL is not the run's.
    /// </summary>
    [Test]
    [Description("A task with a run links its status badge to that run")]
    public async Task Tasks_StatusBadge_OpensTheRun()
    {
        var tasks = new TasksPage(Page, BaseUrl);
        await tasks.NavigateAsync().ConfigureAwait(false);
        var link = tasks.GetRowByText(RunSeeds.LinkedTaskTitle).Locator("a[aria-label^='Open run']");

        await Expect(link).ToHaveCountAsync(1).ConfigureAwait(false);
        await link.ClickAsync().ConfigureAwait(false);

        await Expect(Page).ToHaveURLAsync(new Regex($"/runs/{Runs.Running}$")).ConfigureAwait(false);
    }

    /// <summary>
    ///     The Runs grid keeps the contract's columns at each breakpoint: all seven at 1280 wide; without Started by and
    ///     Updated at 1024; only Status, Current node and Task at 375. At each width every shown header sits over its
    ///     first-row cell, nothing sticks out of the card, the Running run's task link shows inside its cell and the
    ///     grid's visible part, and no column is narrower than its declared width (the Task column's being its minimum).
    ///     A phone scrolls the grid inside its card: at 375 the grid must be wider than the card, and the link is
    ///     scrolled into view inside the grid before it is looked for.
    ///     Red: <c>Visible</c> of Updated always false; at 1280 Updated is missing.
    ///     Red: <c>Visible</c> of the Task column false; at 1280 the task link is missing.
    ///     Red: the grid as before fix round 1, columns hidden by CSS on their cells and no Task minimum; at 1280 the
    ///     Task column is 0px and the link shows -16px of itself.
    ///     Red: <c>Visible</c> of Updated bound to the tablet query; at 1024 Updated shows.
    ///     Red: <c>Visible</c> of Started by always true; at 1024 Started by shows.
    ///     Red: <c>Visible</c> of Process always true; at 375 Process shows.
    ///     Red: <c>Visible</c> of Started always true; at 375 Started shows.
    ///     Red: a header-only CSS rule hides the Process header but not its cells; at 1280 6 headers sit over 7 cells.
    ///     Red: <c>.runs-grid</c> with <c>overflow-x: visible</c>; at 1280 the grid sticks 184px out of its card.
    ///     Red: no grid <c>min-width</c> below the tablet breakpoint; at 375 the grid fits its card and cannot scroll.
    ///     Red: the Task template renders the task as plain text; at 1280 the task link is missing.
    ///     Red: <c>MinGridWidth</c> leaves out the Task column's minimum; at 1280 the link shows -16px of itself.
    ///     Red: the task link is pushed out of its cell with a relative offset; at 1280 none of it shows.
    ///     Red: no scroll into view at 375; the link sits outside the grid's visible part.
    ///     Red: the Task column titled "Board task"; at 1280 the widths check has no Task.
    ///     Red: <c>ProcessWidth</c> 120 against a declared 170; at 1280 the widths check names Process.
    /// </summary>
    [Test]
    [Description("The Runs grid keeps the contract's columns, aligned, at desktop, tablet and phone widths")]
    public async Task Runs_Grid_HidesColumnsOnNarrowScreens()
    {
        await Page.SetViewportSizeAsync(1280, 800).ConfigureAwait(false);
        await _runsPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_runsPage.RowFor(Runs.Failed)).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_runsPage.Header("Updated")).ToBeVisibleAsync().ConfigureAwait(false);
        await ExpectGridLaidOutAsync("1280").ConfigureAwait(false);

        await Page.SetViewportSizeAsync(1024, 800).ConfigureAwait(false);
        await Expect(_runsPage.Header("Started by")).ToBeHiddenAsync().ConfigureAwait(false);
        await Expect(_runsPage.Header("Updated")).ToBeHiddenAsync().ConfigureAwait(false);
        await ExpectGridLaidOutAsync("1024").ConfigureAwait(false);

        await Page.SetViewportSizeAsync(375, 812).ConfigureAwait(false);
        await Expect(_runsPage.Header("Process")).ToBeHiddenAsync().ConfigureAwait(false);
        await Expect(_runsPage.Header("Started")).ToBeHiddenAsync().ConfigureAwait(false);
        await ExpectGridLaidOutAsync("375", phone: true).ConfigureAwait(false);
    }

    /// <summary>
    ///     Every shown header cell over its first-row cell, within 1px in x and width; nothing sticking out of the card;
    ///     the Running run's task link showing inside its cell and the grid's visible part; and no shown column narrower
    ///     than <see cref="DeclaredWidths"/>. On a <paramref name="phone"/> the grid is wider than the card, so it must
    ///     scroll inside it, and the link is scrolled into view inside the grid before it is looked for.
    /// </summary>
    private async Task ExpectGridLaidOutAsync(string viewport, bool phone = false)
    {
        var misaligned = await Page.EvaluateAsync<string>(AlignmentScript).ConfigureAwait(false);
        misaligned.Should().BeEmpty($"each header sits over its cell at {viewport}");

        // The layout's body is the page's scroll container, so the card is measured: nothing may stick out of it.
        var cardOverflow = await Page.Locator("[data-testid='runs-card']").EvaluateAsync<int>("card => card.scrollWidth - card.clientWidth")
            .ConfigureAwait(false);
        cardOverflow.Should().BeLessThanOrEqualTo(0, $"the grid scrolls inside its card, never the page, at {viewport}");

        var link = _runsPage.TaskLink(Runs.Running);
        await Expect(link).ToHaveCountAsync(1).ConfigureAwait(false);
        if (phone)
        {
            var gridOverflow = await link.EvaluateAsync<int>("a => { const g = a.closest('.runs-grid'); return g.scrollWidth - g.clientWidth; }")
                .ConfigureAwait(false);
            gridOverflow.Should().BeGreaterThan(0, $"on a phone the grid is wider than its card and scrolls inside it at {viewport}");
            await link.EvaluateAsync("a => a.closest('td').scrollIntoView({ block: 'nearest', inline: 'nearest' })").ConfigureAwait(false);
        }

        // The link's box clipped to its cell and to the grid's visible part: a link in a column squeezed to nothing, or
        // scrolled out of the grid's view, keeps its own box but shows none of it.
        var shownWidth = await link.EvaluateAsync<double>(ShownWidthScript).ConfigureAwait(false);
        shownWidth.Should().BeGreaterThan(0, phone
            ? $"the task link shows once scrolled into view inside the grid at {viewport}"
            : $"the task link shows in the grid's visible part at {viewport}");

        var widths = JsonSerializer.Deserialize<Dictionary<string, double>>(
            await Page.EvaluateAsync<string>(WidthsScript).ConfigureAwait(false))!;
        widths.Should().ContainKeys("Status", "Current node", "Task");
        // The grid's 1px border on each side comes out of the Task column, the one without a fixed width.
        widths.Where(w => w.Value + 2 < DeclaredWidths[w.Key]).Should()
            .BeEmpty($"no column is narrower than its declared width at {viewport}");
    }

    private const string ShownWidthScript = """
        a => {
            const l = a.getBoundingClientRect();
            const c = a.closest('td').getBoundingClientRect();
            const g = a.closest('.runs-grid').getBoundingClientRect();
            return Math.min(l.right, c.right, g.right) - Math.max(l.left, c.left, g.left);
        }
        """;

    /// <summary>The width RunsGrid declares for each column; the Task column's is its minimum.</summary>
    private static readonly Dictionary<string, double> DeclaredWidths = new(StringComparer.Ordinal)
    {
        ["Status"] = 170,
        ["Process"] = 170,
        ["Current node"] = 140,
        ["Task"] = 200,
        ["Started by"] = 140,
        ["Started"] = 150,
        ["Updated"] = 150,
    };

    private const string AlignmentScript = """
        () => {
            const table = document.querySelector("[data-testid='runs-grid'] table");
            const shown = cell => getComputedStyle(cell).display !== 'none';
            const headers = [...table.querySelectorAll('thead th')].filter(shown);
            const cells = [...table.querySelector('tbody tr').querySelectorAll('td')].filter(shown);
            if (headers.length !== cells.length) return `${headers.length} headers over ${cells.length} cells`;
            return headers.map((h, i) => {
                const a = h.getBoundingClientRect(), b = cells[i].getBoundingClientRect();
                return Math.abs(a.x - b.x) > 1 || Math.abs(a.width - b.width) > 1
                    ? `${h.textContent.trim()}: header ${a.x}+${a.width}, cell ${b.x}+${b.width}` : '';
            }).filter(m => m).join('; ');
        }
        """;

    private const string WidthsScript = """
        () => JSON.stringify(Object.fromEntries([...document.querySelectorAll("[data-testid='runs-grid'] thead th")]
            .filter(h => getComputedStyle(h).display !== 'none')
            .map(h => [h.textContent.trim(), h.getBoundingClientRect().width])))
        """;
}
