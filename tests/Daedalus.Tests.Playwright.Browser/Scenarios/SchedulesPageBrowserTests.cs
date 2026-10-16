using Daedalus.Application.DTOs.Scheduling;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     Phase 2.14, R7: pins what the Schedules page does today, before it moves onto Flux. The schedules a page lists come
///     from configuration and the outbox, and an undelivered run needs a dead-lettered channel message, so the API is
///     answered here with the API's own <see cref="RunDiagnosis"/> serialized as the API serializes it.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Schedules")]
[Description("Schedules: the overview, a row's run history, the deep link and resend")]
public class SchedulesPageBrowserTests : BrowserTestBase
{
    private const string DeliveredName = "Morning digest";
    private const string UndeliveredName = "Weekly digest";
    private const string DeadLetterError = "The channel refused the message.";
    private const string ResendRefusal = "The message is no longer in the outbox.";

    private static readonly Guid DeliveredScheduleId = new Guid(0x5c1e0000, 0x0000, 0x4000, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01);
    private static readonly Guid UndeliveredScheduleId = new Guid(0x5c1e0000, 0x0000, 0x4000, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02);
    private static readonly Guid UndeliveredExecutionId = new Guid(0xe7ec0000, 0x0000, 0x4000, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02);

    private ILocator Desktop => Page.Locator(".schedules-desktop-only");

    private ILocator OverviewRow(string name) =>
        Desktop.Locator("tr.rz-data-row", new LocatorLocatorOptions { HasText = name }).First;

    private ILocator ExpandedRegion => Desktop.Locator(".schedule-expanded-region");

    private static RunDiagnosis Delivered => new(
        RunVerdict.Delivered, DeliveredScheduleId, DeliveredName, new DateTime(2026, 10, 15, 7, 0, 0, DateTimeKind.Utc),
        4, null, null, 1, null, null, new DateTime(2026, 10, 15, 7, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 15, 7, 2, 0, DateTimeKind.Utc), new Guid(0xe7ec0000, 0x0000, 0x4000, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01), true,
        new DateTime(2026, 10, 16, 7, 0, 0, DateTimeKind.Utc), "Two repositories changed.", "Morning: two changes.");

    private static RunDiagnosis Undelivered => new(
        RunVerdict.Undelivered, UndeliveredScheduleId, UndeliveredName, new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
        4, null, null, 1, DeadLetterError, 3, new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 12, 9, 3, 0, DateTimeKind.Utc), UndeliveredExecutionId, true,
        new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc), "Nothing changed this week.", "Weekly: a quiet week.");

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static Task FulfilJsonAsync(IRoute route, object value, int status = 200) =>
        route.FulfillAsync(new RouteFulfillOptions { Status = status, ContentType = "application/json", Body = Json(value) });

    /// <summary>Answers the overview with both schedules, and each schedule's run history with its one run.</summary>
    private async Task RouteSchedulesAsync()
    {
        await Page.RouteAsync(
            url => new Uri(url).AbsolutePath.Equals("/api/schedules", StringComparison.Ordinal),
            route => FulfilJsonAsync(route, new[] { Delivered, Undelivered })).ConfigureAwait(false);
        await Page.RouteAsync(
            url => new Uri(url).AbsolutePath.EndsWith("/runs", StringComparison.Ordinal),
            route => FulfilJsonAsync(
                route,
                route.Request.Url.Contains(UndeliveredScheduleId.ToString(), StringComparison.Ordinal)
                    ? new[] { Undelivered }
                    : new[] { Delivered })).ConfigureAwait(false);
    }

    private async Task NavigateAsync(string path = "/schedules")
    {
        await Page.GotoAsync(new Uri(BaseUrl, path).ToString()).ConfigureAwait(false);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Schedules", Exact = true }))
            .ToBeVisibleAsync().ConfigureAwait(false);
    }

    private ILocator Toggler(string name) => OverviewRow(name).Locator("a[aria-label='Expand child item']");

    private ILocator HistoryHeading(string name) =>
        ExpandedRegion.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = $"Run history — {name}" });

    /// <summary>Opens the page on the deep link to the undelivered schedule, selects its run and returns the run's detail.</summary>
    private async Task<ILocator> OpenUndeliveredRunAsync()
    {
        await NavigateAsync($"/schedules?scheduleId={UndeliveredScheduleId}").ConfigureAwait(false);
        await ExpandedRegion.Locator("tr.rz-data-row").First.ClickAsync().ConfigureAwait(false);
        return ExpandedRegion.Locator(".run-detail-panel");
    }

    /// <summary>
    ///     The overview lists every schedule with its last verdict.
    ///     Red: the Name column renders <c>row.ScheduleId</c>; neither schedule's name shows in its row.
    ///     Red: the Verdict column renders a fixed <c>RunVerdict.Delivered</c> badge; the undelivered row reads Delivered.
    /// </summary>
    [Test]
    [Description("The overview lists each schedule with its last verdict")]
    public async Task The_overview_lists_each_schedule_with_its_last_verdict()
    {
        await RouteSchedulesAsync().ConfigureAwait(false);
        await NavigateAsync().ConfigureAwait(false);

        await Expect(OverviewRow(DeliveredName)).ToContainTextAsync("Delivered").ConfigureAwait(false);
        await Expect(OverviewRow(UndeliveredName)).ToContainTextAsync("Undelivered").ConfigureAwait(false);
    }

    /// <summary>
    ///     A link to <c>/schedules?scheduleId=…</c> opens with that schedule's row expanded, its heading focused, and its
    ///     run history loaded.
    ///     Red: the deep link applies only when <c>_appliedDeepLink</c> is already true; no run history heading shows.
    ///     Red: the deep link expands the first row whose id does not match; no heading names the linked schedule.
    ///     Red: <c>OnRowExpand</c> leaves <c>_focusExpandedPending</c> false; the heading is not focused.
    ///     Red: <c>OnRowExpand</c> does not call <c>SchedulesEffects.EnsureHistoryAsync</c>; the region stays at "Loading run history…".
    ///     Red: the history grid's Step reached column renders <c>"-"</c>; the run does not read Done.
    /// </summary>
    [Test]
    [Description("A deep link opens with its schedule expanded, focused and its runs listed")]
    public async Task A_deep_link_opens_with_its_schedule_expanded_focused_and_its_runs_listed()
    {
        await RouteSchedulesAsync().ConfigureAwait(false);
        await NavigateAsync($"/schedules?scheduleId={UndeliveredScheduleId}").ConfigureAwait(false);

        await Expect(HistoryHeading(UndeliveredName)).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(HistoryHeading(UndeliveredName)).ToBeFocusedAsync().ConfigureAwait(false);
        var historyRow = ExpandedRegion.Locator("tr.rz-data-row").First;
        await Expect(historyRow).ToContainTextAsync("Undelivered").ConfigureAwait(false);
        await Expect(historyRow).ToContainTextAsync("Done").ConfigureAwait(false);
    }

    /// <summary>
    ///     Collapsing the deep-linked row takes the schedule out of the address, and expanding another row puts that one in.
    ///     Red: <c>OnRowCollapse</c> does not navigate; the address keeps <c>scheduleId</c>.
    ///     Red: <c>OnRowExpand</c> does not navigate; the address does not name the other schedule.
    /// </summary>
    [Test]
    [Description("Expanding and collapsing a row keeps the address in step")]
    public async Task Expanding_and_collapsing_a_row_keeps_the_address_in_step()
    {
        await RouteSchedulesAsync().ConfigureAwait(false);
        await NavigateAsync($"/schedules?scheduleId={UndeliveredScheduleId}").ConfigureAwait(false);
        await Expect(HistoryHeading(UndeliveredName)).ToBeVisibleAsync().ConfigureAwait(false);

        await Toggler(UndeliveredName).ClickAsync().ConfigureAwait(false);
        await Expect(HistoryHeading(UndeliveredName)).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(Page).ToHaveURLAsync(new Uri(BaseUrl, "/schedules").ToString()).ConfigureAwait(false);

        await Toggler(DeliveredName).ClickAsync().ConfigureAwait(false);
        await Expect(HistoryHeading(DeliveredName)).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page).ToHaveURLAsync(new Uri(BaseUrl, $"/schedules?scheduleId={DeliveredScheduleId}").ToString())
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     An undelivered run's detail says why it was dead-lettered, and Resend posts that run's execution and says it was
    ///     requeued.
    ///     Red: the Undelivered alert leaves out <c>run.DeadLetterError</c>; the reason does not show.
    ///     Red: <c>SchedulesEffects.ResendAsync</c> posts <c>run.ScheduleId</c> as the execution; the request names the wrong execution.
    ///     Red: <c>SchedulesEffects.ResendAsync</c> returns no outcome on a success; "Requeued." never shows.
    /// </summary>
    [Test]
    [Description("Resend requeues an undelivered run and says so")]
    public async Task Resend_requeues_an_undelivered_run_and_says_so()
    {
        await RouteSchedulesAsync().ConfigureAwait(false);
        var resent = new List<string>();
        await Page.RouteAsync("**/resend", async route =>
        {
            resent.Add(new Uri(route.Request.Url).AbsolutePath);
            await route.FulfillAsync(new RouteFulfillOptions { Status = 200 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var detail = await OpenUndeliveredRunAsync().ConfigureAwait(false);
        await Expect(detail).ToContainTextAsync($"Dead-lettered after 3 retry attempts: {DeadLetterError}").ConfigureAwait(false);
        await detail.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Resend" }).ClickAsync().ConfigureAwait(false);

        await Expect(detail).ToContainTextAsync("Requeued. The verdict will update on the next refresh.").ConfigureAwait(false);
        resent.Should().Equal($"/api/schedules/{UndeliveredScheduleId}/runs/{UndeliveredExecutionId}/resend");
    }

    /// <summary>
    ///     A resend the API refuses shows the refusal's reason and never claims the run was requeued.
    ///     Red: <c>SchedulesEffects.ResendAsync</c> returns a refusal with an empty reason; the reason does not show.
    ///     Red: the page's <c>ResendAsync</c> shows <c>ScheduleResend.Requeued</c> whatever the outcome; "Requeued." shows on a refusal.
    /// </summary>
    [Test]
    [Description("A refused resend says why")]
    public async Task A_refused_resend_says_why()
    {
        await RouteSchedulesAsync().ConfigureAwait(false);
        await Page.RouteAsync("**/resend", route => FulfilJsonAsync(route, new { error = ResendRefusal }, 400)).ConfigureAwait(false);

        var detail = await OpenUndeliveredRunAsync().ConfigureAwait(false);
        await detail.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Resend" }).ClickAsync().ConfigureAwait(false);

        await Expect(detail.Locator(".rz-alert", new LocatorLocatorOptions { HasText = ResendRefusal })).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(detail).Not.ToContainTextAsync("Requeued.").ConfigureAwait(false);
    }

    /// <summary>
    ///     An overview that fails to load says so with a Retry, and Retry loads it again.
    ///     Red: the Error alert shows a fixed "Load failed." in place of <c>View.Error</c>; the failure's text does not show.
    ///     Red: Retry's <c>Click</c> is a no-op; the schedules never show.
    /// </summary>
    [Test]
    [Description("A failed overview offers a Retry that loads it")]
    public async Task A_failed_overview_offers_a_retry_that_loads_it()
    {
        var calls = 0;
        await Page.RouteAsync(
            url => new Uri(url).AbsolutePath.Equals("/api/schedules", StringComparison.Ordinal),
            route => Interlocked.Increment(ref calls) == 1
                ? route.FulfillAsync(new RouteFulfillOptions { Status = 500 })
                : FulfilJsonAsync(route, new[] { Delivered, Undelivered })).ConfigureAwait(false);
        await NavigateAsync().ConfigureAwait(false);

        var alert = Page.Locator(".rz-alert", new PageLocatorOptions { HasText = "500" });
        await Expect(alert).ToContainTextAsync("API error").ConfigureAwait(false);
        await alert.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retry" }).ClickAsync().ConfigureAwait(false);

        await Expect(OverviewRow(UndeliveredName)).ToContainTextAsync("Undelivered").ConfigureAwait(false);
        await Expect(alert).ToHaveCountAsync(0).ConfigureAwait(false);
    }

    /// <summary>
    ///     On a page opened without a deep link, the first click on a row's toggler expands that row and keeps it open.
    ///     Red: <c>OnRowExpand</c> leaves <c>_appliedDeepLink</c> false; the schedule it puts in the address reads as a
    ///     deep link not yet applied, <c>ExpandRow</c> on the open row collapses it, and no run history heading shows.
    ///     Red: <c>OnRowExpand</c> does not navigate; the address has no <c>scheduleId</c>.
    /// </summary>
    [Test]
    [Description("The first expand on a page opened without a deep link keeps the row open")]
    public async Task The_first_expand_keeps_the_row_open()
    {
        await RouteSchedulesAsync().ConfigureAwait(false);
        await NavigateAsync().ConfigureAwait(false);

        await Toggler(UndeliveredName).ClickAsync().ConfigureAwait(false);

        await Expect(HistoryHeading(UndeliveredName)).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page).ToHaveURLAsync(new Uri(BaseUrl, $"/schedules?scheduleId={UndeliveredScheduleId}").ToString())
            .ConfigureAwait(false);
    }
}
