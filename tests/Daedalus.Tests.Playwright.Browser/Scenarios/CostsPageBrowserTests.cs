using System.Collections.Concurrent;
using Daedalus.Application.DTOs;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     Phase 2.14, R7: pins what the Costs page does today, before it moves onto Flux. Its figures are totals over every
///     run, session and history row in the database, which other scenarios add to, so the API is answered here with the
///     API's own cost DTOs serialized as the API serializes them.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Costs")]
[Description("Costs: the KPI cards, the source and project tables, a project's tasks and the estimator")]
public class CostsPageBrowserTests : BrowserTestBase
{
    private const string FirstModelId = "claude-sonnet-test";
    private const string ProjectName = "Widgets";
    private const string TaskTitle = "Ship the widget";
    private const string Scope = "Pinned scope of the faked summary.";

    private static readonly Guid ProjectId = new(0xc0570000, 0x0000, 0x4000, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01);

    private readonly ConcurrentQueue<string> _requests = new();

    private static CostSummaryDto Summary => new(
        1_200_000, 300_000, 4_500, 0, 12.3456m, 42, 0, ExcludedCostDto.None,
        [
            new CostSliceDto(CostSources.History, 200_000, 50_000, 0, 0, 1.2345m, 12),
            new CostSliceDto(CostSources.Manufacture, 1_000_000, 250_000, 4_500, 0, 11.1111m, 30),
        ],
        [new CostSliceDto(FirstModelId, 1_200_000, 300_000, 4_500, 0, 12.3456m, 42)],
        [new CostDayDto(new DateOnly(2026, 10, 15), CostSources.Manufacture, 1_000_000, 250_000, 11.1111m, 30)],
        Scope);

    private static ModelPricingDto[] Pricing =>
    [
        new(FirstModelId, "Sonnet Test", 3m, 15m, 0.3m, 3.75m),
        new("claude-opus-test", "Opus Test", 15m, 75m, 1.5m, 18.75m),
    ];

    private ILocator Card(string testId) => Page.GetByTestId(testId);

    private ILocator Row(string cardTestId, string text) =>
        Card(cardTestId).Locator("tr.rz-data-row", new LocatorLocatorOptions { HasText = text }).First;

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static Task FulfilJsonAsync(IRoute route, object value) =>
        route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = Json(value) });

    /// <summary>
    ///     Answers every cost-analytics call, and records each path and query. The estimate is 3 cents a turn at the
    ///     least and 10 at the most, so the range shows which turn count the page asked for.
    /// </summary>
    private async Task RouteCostsAsync(bool summaryFails = false)
    {
        await Page.RouteAsync("**/api/cost-analytics/**", async route =>
        {
            var url = new Uri(route.Request.Url);
            _requests.Enqueue(url.PathAndQuery);
            var path = url.AbsolutePath;
            if (path.EndsWith("/summary", StringComparison.Ordinal))
            {
                if (summaryFails)
                {
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 500 }).ConfigureAwait(false);
                    return;
                }

                await FulfilJsonAsync(route, Summary).ConfigureAwait(false);
            }
            else if (path.EndsWith("/by-project", StringComparison.Ordinal))
            {
                await FulfilJsonAsync(route, new[]
                {
                    new ProjectCostDto(ProjectId, ProjectName, 200_000, 50_000, 1.2345m, 12, ExcludedCostDto.None, Scope),
                }).ConfigureAwait(false);
            }
            else if (path.EndsWith($"/by-project/{ProjectId}", StringComparison.Ordinal))
            {
                await FulfilJsonAsync(route, new[]
                {
                    new TaskCostDto(Guid.NewGuid(), TaskTitle, 150_000, 40_000, 0.9876m, 9, ExcludedCostDto.None, Scope),
                }).ConfigureAwait(false);
            }
            else if (path.EndsWith("/pricing", StringComparison.Ordinal))
            {
                await FulfilJsonAsync(route, Pricing).ConfigureAwait(false);
            }
            else if (path.EndsWith("/estimate", StringComparison.Ordinal))
            {
                var query = System.Web.HttpUtility.ParseQueryString(url.Query);
                var turns = int.Parse(query["maxIterations"]!, System.Globalization.CultureInfo.InvariantCulture);
                await FulfilJsonAsync(route, new CostEstimateDto(
                    query["modelId"]!, "Sonnet Test", turns, 4000, 1234, turns * 0.03m, turns * 0.1m)).ConfigureAwait(false);
            }
            else
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 404 }).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private async Task NavigateAsync()
    {
        await Page.GotoAsync(new Uri(BaseUrl, "/costs").ToString()).ConfigureAwait(false);
        await Expect(Page.GetByText("Cost Analytics", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     The four KPI cards read the summary: total tokens, priced cost, cache reads and entries, in their short forms.
    ///     Red: Total Tokens shows only <c>TotalInputTokens</c>; it reads 1.2M, not 1.5M.
    ///     Red: Priced Cost shows <c>TotalCost</c> with "C0"; it reads $12, not $12.35.
    ///     Red: Cache Read shows <c>TotalCacheWriteTokens</c>; it reads 0, not 4.5K.
    ///     Red: Recorded Entries shows <c>UnreadableRecords</c>; it reads 0, not 42.
    /// </summary>
    [Test]
    [Description("The KPI cards read the summary")]
    public async Task The_kpi_cards_read_the_summary()
    {
        await RouteCostsAsync().ConfigureAwait(false);
        await NavigateAsync().ConfigureAwait(false);

        await Expect(Card("stat-total-tokens")).ToContainTextAsync("1.5M").ConfigureAwait(false);
        await Expect(Card("stat-total-cost")).ToContainTextAsync("$12.35").ConfigureAwait(false);
        await Expect(Card("stat-cache-read")).ToContainTextAsync("4.5K").ConfigureAwait(false);
        await Expect(Card("stat-total-entries")).ToContainTextAsync("42").ConfigureAwait(false);
    }

    /// <summary>
    ///     The By Source table lists each source with its tokens and cost, and the project table lists each project's
    ///     history.
    ///     Red: the By Source grid binds <c>_summary.ByModel</c>; no manufacture row shows.
    ///     Red: the Source Input column formats with "{0:N2}"; the row reads 1,000,000.00.
    ///     Red: the Source Cost column formats with "{0:C2}"; the row reads $11.11, not $11.1111.
    ///     Red: the project grid binds an empty list; no Widgets row shows.
    ///     Red: the project Total Cost column formats with "{0:C2}"; the row reads $1.23, not $1.2345.
    /// </summary>
    [Test]
    [Description("The source and project tables list their rows")]
    public async Task The_source_and_project_tables_list_their_rows()
    {
        await RouteCostsAsync().ConfigureAwait(false);
        await NavigateAsync().ConfigureAwait(false);

        var source = Row("cost-by-source", CostSources.Manufacture);
        await Expect(source).ToContainTextAsync("1,000,000").ConfigureAwait(false);
        await Expect(source).Not.ToContainTextAsync("1,000,000.00").ConfigureAwait(false);
        await Expect(source).ToContainTextAsync("$11.1111").ConfigureAwait(false);
        var project = Row("project-costs", ProjectName);
        await Expect(project).ToContainTextAsync("200,000").ConfigureAwait(false);
        await Expect(project).ToContainTextAsync("$1.2345").ConfigureAwait(false);
    }

    /// <summary>
    ///     Expanding a project loads that project's tasks once and lists them under it.
    ///     Red: <c>OnRowExpand</c> returns before the call; no task row shows.
    ///     Red: <c>GetTaskCosts</c> always returns an empty list; the task row never shows.
    ///     Red: the task Cost column formats with "{0:C2}"; the row reads $0.99, not $0.9876.
    ///     Red: <c>OnRowExpand</c> starts <c>CostsEffects.LoadTaskCostsAsync</c> twice at once; the project's path is requested two times.
    /// </summary>
    [Test]
    [Description("Expanding a project lists its tasks")]
    public async Task Expanding_a_project_lists_its_tasks()
    {
        await RouteCostsAsync().ConfigureAwait(false);
        await NavigateAsync().ConfigureAwait(false);

        await Row("project-costs", ProjectName).Locator("a[aria-label='Expand child item']").ClickAsync().ConfigureAwait(false);

        var task = Row("project-costs", TaskTitle);
        await Expect(task).ToContainTextAsync("150,000").ConfigureAwait(false);
        await Expect(task).ToContainTextAsync("$0.9876").ConfigureAwait(false);
        _requests.Should().ContainSingle(r => r == $"/api/cost-analytics/by-project/{ProjectId}");
    }

    /// <summary>
    ///     The estimator starts on the first priced model at 10 turns and 4,000 prompt tokens, shows the estimate's range,
    ///     and asks again when the turns change.
    ///     Red: the <c>CostsLoaded</c> reducer keeps the estimator's model; the request names the page's default model.
    ///     Red: the range shows <c>EstimatedMaxCost</c> twice; it reads "$1.00 — $1.00".
    ///     Red: the caption leaves out <c>EstimatedResponseTokens</c>; "Based on 1234" does not show.
    ///     Red: the Turns input's <c>ValueChanged</c> does not call <c>EstimateAsync</c>; the range stays at 10 turns' "$0.30 — $1.00".
    /// </summary>
    [Test]
    [Description("The estimator shows the range and asks again when the turns change")]
    public async Task The_estimator_shows_the_range_and_asks_again_when_the_turns_change()
    {
        await RouteCostsAsync().ConfigureAwait(false);
        await NavigateAsync().ConfigureAwait(false);

        var estimator = Card("cost-estimator");
        await Expect(estimator).ToContainTextAsync("$0.30 — $1.00").ConfigureAwait(false);
        await Expect(estimator).ToContainTextAsync("Based on 1234 average output tokens per turn").ConfigureAwait(false);
        var estimate = System.Web.HttpUtility.ParseQueryString(
            new Uri(BaseUrl, _requests.First(r => r.StartsWith("/api/cost-analytics/estimate?", StringComparison.Ordinal))).Query);
        estimate["modelId"].Should().Be(FirstModelId);
        estimate["maxIterations"].Should().Be("10");
        estimate["estimatedPromptTokens"].Should().Be("4000");

        var turns = estimator.Locator("input[name='Turns']");
        await turns.FillAsync("20").ConfigureAwait(false);
        await turns.PressAsync("Tab").ConfigureAwait(false);

        await Expect(estimator).ToContainTextAsync("$0.60 — $2.00").ConfigureAwait(false);
    }

    /// <summary>
    ///     A summary that fails to load says so in an alert, and the rest of the page still loads. The page has no Retry;
    ///     a reload is the way back.
    ///     Red: <c>CostsEffects.LoadAsync</c> passes a null error in <c>CostsLoaded</c>; no alert shows.
    ///     Red: <c>CostsEffects.LoadAsync</c> returns before <c>CostsLoaded</c> when the summary fails; the pricing table is empty.
    /// </summary>
    [Test]
    [Description("A failed summary says so")]
    public async Task A_failed_summary_says_so()
    {
        await RouteCostsAsync(summaryFails: true).ConfigureAwait(false);
        await NavigateAsync().ConfigureAwait(false);

        await Expect(Page.Locator(".rz-alert", new PageLocatorOptions { HasText = "API error" }))
            .ToContainTextAsync("500").ConfigureAwait(false);
        await Expect(Card("model-pricing")).ToContainTextAsync("Sonnet Test").ConfigureAwait(false);
    }
}
