using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Daedalus.Agents.Workflow;
using Daedalus.Tests.Playwright.Browser.Fixtures;
using Daedalus.Tests.Playwright.Browser.PageObjects;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     The run page explains itself: why a failed run failed, who started it, every review visit with its findings and
///     the answer implement gave, and a timeline without empty badges. Each scenario seeds the runs it reads through the
///     real store and record store (<see cref="RunSeeds"/>), so the shared seeded runs stay as they are.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Runs")]
[Description("The run page: failure reason, starter, review visits, timeline badges and the pull request link")]
public sealed class RunPageBrowserTests : BrowserTestBase
{
    private const string RetryFile = "src/Retry.cs";
    private const string FirstScenario = "A flaky dependency retries forever.";
    private const string SecondScenario = "The cap is read before the setting loads.";
    private const string Answer = "Capped the retry loop at three tries.";

    private RunDetailPage _runPage = null!;

    private static RunSeeds Runs => E2EServerFixture.Runs;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted)
            return;
        _runPage = new RunDetailPage(Page, BaseUrl);
    }

    /// <summary>
    ///     A failed run's alert gives the derived reason, not the host's raw last error, and keeps the Failed badge; a run
    ///     whose view carries no reason says where to look instead.
    ///     Red: <c>RunHeader</c> shows <c>Run.LastError</c> in the Failed alert; it reads "The implement node stopped.".
    ///     Red: <c>RunHeader</c> drops the fallback text; with no reason the alert is empty and the fallback is missing.
    ///     Red: <c>RunHeader</c> shows the reason in place of the status badge; the badge is missing.
    /// </summary>
    [Test]
    [Description("A failed run shows why it failed")]
    public async Task A_failed_run_shows_its_reason()
    {
        var run = await Runs.StartAsync($"e2e-blocked-{Guid.NewGuid():N}", CancellationToken.None).ConfigureAwait(false);
        await Runs.CompleteAsync(run, Moving("review"), CancellationToken.None, Outcome("blocked")).ConfigureAwait(false);
        await Runs.Store.FailAsync(run, "The implement node stopped.", CancellationToken.None).ConfigureAwait(false);

        await _runPage.NavigateAsync(run).ConfigureAwait(false);

        await Expect(_runPage.Failure).ToContainTextAsync("Implement reported it is blocked.").ConfigureAwait(false);
        await Expect(_runPage.Failure).Not.ToContainTextAsync("The implement node stopped.").ConfigureAwait(false);
        await Expect(_runPage.Status).ToHaveTextAsync("Failed").ConfigureAwait(false);

        await Page.RouteAsync($"**/api/workflow-runs/{run}", async route =>
        {
            var response = await route.FetchAsync().ConfigureAwait(false);
            var view = JsonNode.Parse(await response.TextAsync().ConfigureAwait(false))!;
            view["failureReason"] = null;
            await route.FulfillAsync(new RouteFulfillOptions { Response = response, Body = view.ToJsonString() }).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await _runPage.NavigateAsync(run).ConfigureAwait(false);

        await Expect(_runPage.Failure).ToContainTextAsync("The run failed; the reason is in the timeline.").ConfigureAwait(false);
    }

    /// <summary>
    ///     The header names the starter by display name, and a run whose starter has none reads "unknown" in the header and
    ///     on the Runs list, never the subject id.
    ///     Red: <c>RunHeader</c> falls back to <c>Run.StartedBy</c>; the header shows the id.
    ///     Red: <c>RunsGrid</c> falls back to <c>run.StartedBy</c>; the row shows the id.
    ///     Red: <c>RunHeader</c> shows <c>Run.StartedBy</c> first; the named run reads "e2e-test-user".
    /// </summary>
    [Test]
    [Description("Started by shows a name, or unknown, never a subject id")]
    public async Task Started_by_shows_a_name_not_a_guid()
    {
        var subject = Guid.NewGuid().ToString();
        var run = await Runs.StartAsync($"e2e-nameless-{Guid.NewGuid():N}", CancellationToken.None, new RunPrincipal(subject, ["admin"]))
            .ConfigureAwait(false);

        await _runPage.NavigateAsync(run).ConfigureAwait(false);
        await Expect(_runPage.Header).ToContainTextAsync("started by unknown").ConfigureAwait(false);
        await Expect(_runPage.Header).Not.ToContainTextAsync(subject).ConfigureAwait(false);

        await Page.SetViewportSizeAsync(1280, 800).ConfigureAwait(false);
        var runs = new RunsPage(Page, BaseUrl);
        await runs.NavigateAsync().ConfigureAwait(false);
        await Expect(runs.RowFor(run)).ToContainTextAsync("unknown").ConfigureAwait(false);
        await Expect(runs.RowFor(run)).Not.ToContainTextAsync(subject).ConfigureAwait(false);

        await _runPage.NavigateAsync(Runs.Succeeded).ConfigureAwait(false);
        await Expect(_runPage.Header).ToContainTextAsync($"started by {RunSeeds.StarterName}").ConfigureAwait(false);
    }

    /// <summary>
    ///     The Review tab lists all three visits newest first, each with its number and verdict; the first rejection names
    ///     its lens, opens to what it checked and its finding as <c>file:line</c> and scenario, and shows implement's
    ///     answer under "How implement answered"; the approval shows no answer block. A run with no review says so.
    ///     Red: <c>ReviewVisits</c> lists <c>Run.ReviewVisits.Take(1)</c>; one visit instead of three.
    ///     Red: <c>ReviewVisits</c> numbers visits from the newest; the first reads "Visit 1".
    ///     Red: <c>ReviewVisits</c> shows the badge text "Passed" for Approved; the verdicts differ.
    ///     Red: <c>ReviewVisits</c> leaves out the rejecting lens; visit 1's header lacks "rejected by mechanism".
    ///     Red: <c>ReviewVisits</c> renders a finding as <c>file</c> only; "src/Retry.cs:12" is missing.
    ///     Red: <c>ReviewVisits</c> leaves out the scenario; the scenario is missing.
    ///     Red: <c>ReviewVisits</c> leaves out <c>checked</c>; "src/Feature.cs" is missing from the open lens.
    ///     Red: <c>ReviewVisits</c> drops the answer text; the answer block lacks it.
    ///     Red: <c>ReviewVisits</c> shows the answer block on an approval too; visit 3 has one.
    ///     Red: <c>RunDetail</c> keeps <c>ReviewEvidence</c>'s latest-only Checked list on the tab; the visits list is missing.
    ///     Red: <c>ReviewVisits</c> drops the empty state; the running run's tab lacks "No review has run yet.".
    /// </summary>
    [Test]
    [Description("The review tab lists every visit with its findings and implement's answer")]
    public async Task The_review_tab_lists_every_visit_with_findings_and_the_answer()
    {
        var run = await SeedReviewHistoryAsync().ConfigureAwait(false);

        await _runPage.NavigateAsync(run).ConfigureAwait(false);
        await _runPage.OpenReviewTabAsync().ConfigureAwait(false);

        await Expect(_runPage.Visits).ToHaveCountAsync(3).ConfigureAwait(false);
        var newest = _runPage.Visits.Nth(0);
        var first = _runPage.Visits.Nth(2);
        await Expect(_runPage.Visits.Locator("[data-testid='review-visit-title']")).ToHaveTextAsync(["Visit 3", "Visit 2", "Visit 1"])
            .ConfigureAwait(false);
        await Expect(_runPage.Visits.Locator("[data-testid='review-visit-verdict']")).ToHaveTextAsync(["Approved", "Rejected", "Rejected"])
            .ConfigureAwait(false);
        await Expect(first).ToContainTextAsync("rejected by mechanism").ConfigureAwait(false);

        await RunDetailPage.LensToggle(first, "mechanism").ClickAsync().ConfigureAwait(false);
        var lens = first.Locator("[data-testid='review-lens-body']:visible");
        await Expect(lens).ToContainTextAsync($"{RetryFile}:12").ConfigureAwait(false);
        await Expect(lens).ToContainTextAsync(FirstScenario).ConfigureAwait(false);
        await Expect(lens).ToContainTextAsync("src/Feature.cs").ConfigureAwait(false);

        await Expect(RunDetailPage.Answer(first)).ToContainTextAsync("How implement answered").ConfigureAwait(false);
        await Expect(RunDetailPage.Answer(first)).ToContainTextAsync(Answer).ConfigureAwait(false);
        await Expect(RunDetailPage.Answer(newest)).ToHaveCountAsync(0).ConfigureAwait(false);

        await _runPage.NavigateAsync(Runs.Running).ConfigureAwait(false);
        await _runPage.OpenReviewTabAsync().ConfigureAwait(false);
        await Expect(_runPage.ReviewVisits).ToContainTextAsync("No review has run yet.").ConfigureAwait(false);
    }

    /// <summary>
    ///     A rejection whose implement visit recorded no answer says "No response recorded" under the heading, and does not
    ///     borrow the earlier visit's answer.
    ///     Red: <c>ReviewVisits</c> renders nothing when <c>ReviewResponse</c> is null; the answer block is missing.
    ///     Red: <c>ReviewVisits</c> drops the heading when there is no answer; "How implement answered" is missing.
    /// </summary>
    [Test]
    [Description("A visit without a response says none recorded")]
    public async Task A_visit_without_a_response_says_none_recorded()
    {
        var run = await SeedReviewHistoryAsync().ConfigureAwait(false);

        await _runPage.NavigateAsync(run).ConfigureAwait(false);
        await _runPage.OpenReviewTabAsync().ConfigureAwait(false);

        var second = _runPage.Visits.Nth(1);
        await Expect(RunDetailPage.Answer(second)).ToContainTextAsync("How implement answered").ConfigureAwait(false);
        await Expect(RunDetailPage.Answer(second)).ToContainTextAsync("No response recorded").ConfigureAwait(false);
        await Expect(RunDetailPage.Answer(second)).Not.ToContainTextAsync(Answer).ConfigureAwait(false);
    }

    /// <summary>
    ///     A visit with a record that cannot be read wears a Warning badge reading "Unreadable", never Approved, shows the
    ///     answer block as a rejection does, and the tab shows the view's error in an alert.
    ///     Red: <c>RunStatusLabels.VerdictStyle</c> maps Unreadable to Success; the badge lacks <c>rz-badge-warning</c>.
    ///     Red: <c>ReviewVisits</c> shows the answer block on a Rejected visit only; the Unreadable visit has none.
    ///     Red: <c>ReviewVisits</c> drops the error alert; it is missing.
    /// </summary>
    [Test]
    [Description("An unreadable visit is a Warning badge reading Unreadable, with the error shown")]
    public async Task An_unreadable_visit_never_reads_as_approved()
    {
        var run = await Runs.StartAsync($"e2e-unreadable-{Guid.NewGuid():N}", CancellationToken.None).ConfigureAwait(false);
        await Runs.CompleteAsync(run, Moving("review"), CancellationToken.None).ConfigureAwait(false);
        var review = await Runs.CompleteAsync(run, Moving("implement"), CancellationToken.None, Outcome("rejected")).ConfigureAwait(false);
        await AppendEvidenceAsync(run, review, "correctness", approved: true, ["src/Feature.cs"]).ConfigureAwait(false);
        await Runs.AppendRecordAsync(run, review, "review", WorkflowRunRecord.ReviewEvidenceKind, """{"lens":"mechanism"}""", CancellationToken.None)
            .ConfigureAwait(false);

        await _runPage.NavigateAsync(run).ConfigureAwait(false);
        await _runPage.OpenReviewTabAsync().ConfigureAwait(false);

        var verdict = RunDetailPage.Verdict(_runPage.Visits.Nth(0));
        await Expect(verdict).ToHaveTextAsync("Unreadable").ConfigureAwait(false);
        await Expect(verdict).ToHaveClassAsync(new Regex(@"(^|\s)rz-badge-warning(\s|$)")).ConfigureAwait(false);
        await Expect(RunDetailPage.Answer(_runPage.Visits.Nth(0))).ToContainTextAsync("No response recorded").ConfigureAwait(false);
        await Expect(_runPage.ReviewVisitsError).ToContainTextAsync("review-evidence record(s) could not be read").ConfigureAwait(false);
    }

    /// <summary>
    ///     A completion whose outcome is <c>none</c> renders no badge; the step before it keeps its own.
    ///     Red: <c>RunTimeline</c> renders the badge whatever <c>RunStatusLabels.ShowsStepBadge</c> says; step 2 has one.
    ///     Red: <c>RunTimeline</c> hides every badge; step 1 has none.
    /// </summary>
    [Test]
    [Description("A step with outcome none has no badge")]
    public async Task A_step_with_outcome_none_has_no_badge()
    {
        var run = await Runs.StartAsync($"e2e-none-{Guid.NewGuid():N}", CancellationToken.None).ConfigureAwait(false);
        await Runs.CompleteAsync(run, Moving("review"), CancellationToken.None, Outcome("none")).ConfigureAwait(false);

        await _runPage.NavigateAsync(run).ConfigureAwait(false);

        await Expect(_runPage.Steps).ToHaveCountAsync(2).ConfigureAwait(false);
        await Expect(_runPage.Steps.Nth(0).Locator(".rz-badge")).ToHaveCountAsync(1).ConfigureAwait(false);
        await Expect(_runPage.Steps.Nth(1).Locator(".rz-badge")).ToHaveCountAsync(0).ConfigureAwait(false);
    }

    /// <summary>
    ///     The pull request link is a Radzen link in the primary colour, with an external-link icon that is not read out,
    ///     named for its pull request.
    ///     Red: <c>RunHeader</c>'s link without <c>Icon</c>; the icon is missing.
    ///     Red: <c>RunHeader</c>'s link styled <c>color: var(--rz-text-color)</c>; its colour is not the primary one.
    ///     Red: drop <c>aria-hidden</c> from the icon; the icon is not hidden from assistive technology.
    ///     Red: drop the link's <c>aria-label</c>; its accessible name includes "open_in_new".
    /// </summary>
    [Test]
    [Description("The pull request link is a styled Radzen link with an external-link icon")]
    public async Task The_pull_request_is_a_styled_link()
    {
        await _runPage.NavigateAsync(Runs.Succeeded).ConfigureAwait(false);

        var link = Page.Locator("[data-testid='run-pr-link']");
        await Expect(link).ToHaveClassAsync(new Regex(@"(^|\s)rz-link(\s|$)")).ConfigureAwait(false);
        await Expect(link).ToHaveAttributeAsync("href", RunSeeds.PullRequestUrl).ConfigureAwait(false);
        var icon = link.Locator(".rzi");
        await Expect(icon).ToHaveTextAsync("open_in_new").ConfigureAwait(false);
        await Expect(icon).ToHaveAttributeAsync("aria-hidden", "true").ConfigureAwait(false);
        await Expect(link).ToHaveAccessibleNameAsync("Pull request #42, opens in a new tab").ConfigureAwait(false);

        var colours = await link.EvaluateAsync<string[]>("""
            a => {
                const probe = document.createElement('span');
                probe.style.color = 'var(--rz-primary)';
                document.body.appendChild(probe);
                const primary = getComputedStyle(probe).color;
                probe.remove();
                return [getComputedStyle(a).color, primary];
            }
            """).ConfigureAwait(false);
        colours[0].Should().Be(colours[1], "the pull request link wears the primary token");
    }

    /// <summary>
    ///     Each lens section opens with a button that says whether it is open. The section it controls is always in the
    ///     page, so <c>aria-controls</c> names an element even while closed, and shows only when open.
    ///     Red: <c>ReviewVisits</c> renders the lens heading as a <c>div</c>; there is no button.
    ///     Red: <c>ReviewVisits</c> leaves <c>aria-expanded</c> at "false" after the click; it never reads "true".
    ///     Red: <c>ReviewVisits</c> drops <c>hidden</c> from the section; it is visible while collapsed.
    ///     Red: <c>ReviewVisits</c> renders the section only while open, as before fix round 1; the controlled element
    ///     is missing while collapsed.
    ///     Red: <c>ReviewVisits</c> points <c>aria-controls</c> at an id nothing has; the controlled section is missing.
    ///     Red: <c>ReviewVisits</c> sets <c>hidden</c> always; the section stays hidden after the click.
    /// </summary>
    [Test]
    [Description("Lens sections are buttons with aria-expanded")]
    public async Task Lens_sections_are_buttons_with_aria_expanded()
    {
        var run = await SeedReviewHistoryAsync().ConfigureAwait(false);

        await _runPage.NavigateAsync(run).ConfigureAwait(false);
        await _runPage.OpenReviewTabAsync().ConfigureAwait(false);

        var first = _runPage.Visits.Nth(2);
        var toggle = RunDetailPage.LensToggle(first, "mechanism");
        await Expect(toggle).ToHaveRoleAsync(AriaRole.Button).ConfigureAwait(false);
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false").ConfigureAwait(false);
        var controls = await toggle.GetAttributeAsync("aria-controls").ConfigureAwait(false);
        controls.Should().NotBeNullOrEmpty();
        var section = Page.Locator($"#{controls}");
        await Expect(section).ToHaveCountAsync(1).ConfigureAwait(false);
        await Expect(section).ToBeHiddenAsync().ConfigureAwait(false);

        await toggle.ClickAsync().ConfigureAwait(false);

        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true").ConfigureAwait(false);
        await Expect(section).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(section).ToContainTextAsync(FirstScenario).ConfigureAwait(false);

        await toggle.ClickAsync().ConfigureAwait(false);
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false").ConfigureAwait(false);
    }

    /// <summary>
    ///     The Runs list shows Started and Updated in the viewer's time zone. The browser runs in Pacific/Auckland, so a
    ///     UTC time differs by hours.
    ///     Red: <c>RunsGrid</c>'s Started column with <c>FormatString="{0:g}"</c>, as before; the cell shows UTC.
    ///     Red: <c>RunsGrid</c>'s Updated column with <c>FormatString="{0:g}"</c>, as before; the cell shows UTC.
    /// </summary>
    [Test]
    [Description("The Runs list shows times in the viewer's time zone")]
    public async Task The_runs_list_shows_local_times()
    {
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var list = await GetApiAsync<JsonElement>("/api/workflow-runs?status=Failed&take=100").ConfigureAwait(false);
        var item = list.GetProperty("runs").EnumerateArray()
            .First(r => r.GetProperty("id").GetGuid() == Runs.Failed);
        string Local(string property) => TimeZoneInfo.ConvertTime(item.GetProperty(property).GetDateTimeOffset(), auckland)
            .ToString("g", CultureInfo.GetCultureInfo("en-US"));

        var context = await NewTracedContextAsync(new BrowserNewContextOptions
        {
            TimezoneId = "Pacific/Auckland",
            Locale = "en-US",
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var runs = new RunsPage(page, BaseUrl);
        await runs.NavigateAsync().ConfigureAwait(false);

        var row = runs.RowFor(Runs.Failed);
        await Expect(runs.Header("Updated")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(row.Locator("td").Nth(5)).ToHaveTextAsync(Local("createdAt")).ConfigureAwait(false);
        await Expect(row.Locator("td").Nth(6)).ToHaveTextAsync(Local("updatedAt")).ConfigureAwait(false);
    }

    /// <summary>
    ///     A run with three review visits: visit 1 rejected by mechanism with a finding, answered by implement; visit 2
    ///     rejected again, its implement visit recording no answer; visit 3 approved, and the run succeeded.
    /// </summary>
    private static async Task<Guid> SeedReviewHistoryAsync()
    {
        var ct = CancellationToken.None;
        var run = await Runs.StartAsync($"e2e-review-history-{Guid.NewGuid():N}", ct).ConfigureAwait(false);
        await Runs.CompleteAsync(run, Moving("review"), ct).ConfigureAwait(false);

        var first = await Runs.CompleteAsync(run, Moving("implement"), ct, Outcome("rejected")).ConfigureAwait(false);
        await AppendEvidenceAsync(run, first, "correctness", approved: true, ["src/Feature.cs"]).ConfigureAwait(false);
        await AppendEvidenceAsync(run, first, "mechanism", approved: false, ["src/Feature.cs", RetryFile],
            new { file = RetryFile, line = 12, scenario = FirstScenario }).ConfigureAwait(false);

        var answered = await Runs.CompleteAsync(run, Moving("review"), ct).ConfigureAwait(false);
        await Runs.AppendRecordAsync(
                run, answered, "implement", WorkflowRunRecord.ReviewResponseKind, ReviewResponseRecord.ToPayloadJson(Answer), ct)
            .ConfigureAwait(false);

        var second = await Runs.CompleteAsync(run, Moving("implement"), ct, Outcome("rejected")).ConfigureAwait(false);
        await AppendEvidenceAsync(run, second, "mechanism", approved: false, [RetryFile],
            new { file = RetryFile, line = 30, scenario = SecondScenario }).ConfigureAwait(false);

        await Runs.CompleteAsync(run, Moving("review"), ct).ConfigureAwait(false);

        var third = await Runs.CompleteAsync(
                run, new WorkflowTransition("done", WorkflowStatus.Succeeded, null, WorkflowEventKind.Completed), ct, Outcome("approved"))
            .ConfigureAwait(false);
        await AppendEvidenceAsync(run, third, "correctness", approved: true, ["src/Feature.cs"]).ConfigureAwait(false);
        await AppendEvidenceAsync(run, third, "mechanism", approved: true, [RetryFile]).ConfigureAwait(false);
        return run;
    }

    /// <summary>One review-evidence record, in the shape <c>ReviewLensRunner</c> writes.</summary>
    private static Task AppendEvidenceAsync(Guid run, long seq, string lens, bool approved, string[] examined, params object[] findings) =>
        Runs.AppendRecordAsync(
            run, seq, "review", WorkflowRunRecord.ReviewEvidenceKind,
            JsonSerializer.Serialize(new
            {
                lens,
                verdict = approved ? ReviewEvidence.Approved : ReviewEvidence.Rejected,
                @checked = examined,
                deferred = Array.Empty<object>(),
                findings,
            }),
            CancellationToken.None);

    private static WorkflowTransition Moving(string node) =>
        new(node, WorkflowStatus.Running, null, WorkflowEventKind.Completed);

    private static NodeResult Outcome(string outcome) =>
        new(outcome, new Dictionary<string, object?>(StringComparer.Ordinal));
}
