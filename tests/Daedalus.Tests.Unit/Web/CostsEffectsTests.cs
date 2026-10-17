using System.Net;
using System.Text.Json;
using Daedalus.Application.DTOs;
using Daedalus.Web.State.Costs;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The Costs effects: the page's read and the estimate after it, the estimator, a project's tasks, and what they never
///     store: a session being renewed, or a read whose page went.
/// </summary>
public sealed class CostsEffectsTests : IDisposable
{
    private const string SummaryPath = "/api/cost-analytics/summary";
    private const string ProjectsPath = "/api/cost-analytics/by-project";
    private const string PricingPath = "/api/cost-analytics/pricing";

    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly string TasksPath = $"/api/cost-analytics/by-project/{ProjectId}";

    private static readonly CostSummaryDto Summary = new(1, 2, 3, 0, 1.5m, 4, 0, ExcludedCostDto.None, [], [], [], "scope");

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<CostsState> _store;
    private readonly CostsEffects _effects;
    private readonly RecordingLogger<CostsEffects> _logger = new();

    public CostsEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<CostsState>>();
        _effects = new CostsEffects(_handler.Client(), _provider.GetRequiredService<IDispatcher>(), _store, _logger);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static string EstimatePath(string model, int turns = 10, int prompt = 4000) =>
        $"/api/cost-analytics/estimate?modelId={model}&maxIterations={turns}&estimatedPromptTokens={prompt}";

    private static CostEstimateDto Estimate(string model, int turns) => new(model, model, turns, 4000, 1234, turns * 0.03m, turns * 0.1m);

    private static HttpResponseMessage SigningIn()
    {
        var navigation = new RecordingNavigation();
        throw new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null);
    }

    private static HttpResponseMessage ServerError() => new(HttpStatusCode.InternalServerError);

    /// <summary>Answers the page's three reads, and the estimate for the first priced model.</summary>
    private void RouteAll()
    {
        _handler.OnJson(SummaryPath, Json(Summary))
            .OnJson(ProjectsPath, Json(new[] { new ProjectCostDto(ProjectId, "Widgets", 1, 2, 0.5m, 3, ExcludedCostDto.None, "s") }))
            .OnJson(PricingPath, Json(new[] { new ModelPricingDto("sonnet", "Sonnet", 3m, 15m, 0.3m, 3.75m) }))
            .OnJson(EstimatePath("sonnet"), Json(Estimate("sonnet", 10)));
    }

    /// <summary>
    ///     The page reads the summary, the projects and the pricing, then estimates for the first priced model.
    ///     Red: dispatch nothing on the answer in LoadAsync; the summary stays null.
    ///     Red: drop the EstimateAsync call from LoadAsync; no estimate is asked for.
    ///     Red: estimate with CostEstimator.Default in LoadAsync; the estimate names the default model.
    /// </summary>
    [Fact]
    public async Task The_page_reads_its_figures_and_estimates_for_the_first_priced_model()
    {
        RouteAll();

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Summary.Should().BeEquivalentTo(Summary);
        _store.Value.Projects.Should().ContainSingle();
        _store.Value.Pricing.Should().ContainSingle();
        _store.Value.Estimate.Should().Be(Estimate("sonnet", 10));
        _handler.Paths.Should().Equal(SummaryPath, ProjectsPath, PricingPath, EstimatePath("sonnet"));
    }

    /// <summary>
    ///     A summary that fails is the error and is logged as a warning; the rest of the page still lands.
    ///     Red: pass a null error in LoadAsync; the error stays null.
    ///     Red: drop the LogLoadFailed call; no warning is written.
    /// </summary>
    [Fact]
    public async Task A_failed_summary_is_the_error_and_logs_a_warning()
    {
        RouteAll();
        _handler.On(SummaryPath, ServerError);

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().Contain("500");
        _store.Value.Pricing.Should().ContainSingle();
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    ///     The projects or the pricing failing shows no error, as the page always did: the table is empty. Each is logged.
    ///     With no pricing there is no model to estimate for, so nothing is estimated (#385).
    ///     Red: pass the projects' error as the page's error in LoadAsync; the error is set.
    ///     Red: drop the LogLoadFailed calls for the projects and the pricing; fewer than two warnings are written.
    ///     Red: estimate whatever the estimator's model in LoadAsync; an estimate is asked for with no model.
    /// </summary>
    [Fact]
    public async Task A_failed_table_is_empty_and_logged()
    {
        RouteAll();
        _handler.On(ProjectsPath, ServerError).On(PricingPath, ServerError);

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().BeNull();
        _store.Value.Projects.Should().BeEmpty();
        _store.Value.Pricing.Should().BeEmpty();
        _logger.Entries.Should().HaveCount(2).And.OnlyContain(e => e.Level == LogLevel.Warning);
        _handler.Paths.Should().Equal(SummaryPath, ProjectsPath, PricingPath);
    }

    /// <summary>
    ///     A summary whose token cannot be refreshed stores no error and is not logged: the layout says so.
    ///     Red: pass the summary's error whatever it is in LoadAsync; the error is the signing-in text.
    ///     Red: log the summary's failure before the SigningInAgain check; the renewal writes a warning.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_stores_no_error()
    {
        RouteAll();
        _handler.On(SummaryPath, SigningIn);

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().BeNull();
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     A caller already gone sends nothing and dispatches nothing: the sequence stays.
    ///     Red: drop the cancelled-token guard at the top of LoadAsync; the sequence moves.
    /// </summary>
    [Fact]
    public async Task A_read_for_a_page_already_gone_changes_nothing()
    {
        RouteAll();
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();

        await _effects.LoadAsync(gone.Token);

        _store.Value.Sequence.Should().Be(0);
        _handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     A read whose page closes while it is in flight stores nothing, logs nothing and asks for no estimate.
    ///     Red: drop the cancelled-token guard after the reads in LoadAsync; the cancelled reads are logged.
    /// </summary>
    [Fact]
    public async Task A_read_whose_page_closes_stores_nothing()
    {
        RouteAll();
        using var closing = new CancellationTokenSource();
        _handler.On(SummaryPath, async () =>
        {
            await closing.CancelAsync();
            return RecordingHandler.Json(HttpStatusCode.OK, Json(Summary));
        });

        await _effects.LoadAsync(closing.Token);

        _store.Value.Summary.Should().BeNull();
        _logger.Entries.Should().BeEmpty();
        _handler.Paths.Should().NotContain(EstimatePath("sonnet"));
    }

    /// <summary>
    ///     A change of the estimator's inputs asks for an estimate with them and shows it.
    ///     Red: send CostEstimator.Default.Turns in EstimateAsync; the request asks for 10 turns.
    ///     Red: dispatch nothing on the answer in EstimateAsync; the estimate stays null.
    /// </summary>
    [Fact]
    public async Task New_inputs_ask_for_their_estimate()
    {
        _handler.OnJson(EstimatePath("opus", 20, 5000), Json(Estimate("opus", 20)));

        await _effects.EstimateAsync(new CostEstimator("opus", 20, 5000), CancellationToken.None);

        _store.Value.Estimator.Should().Be(new CostEstimator("opus", 20, 5000));
        _store.Value.Estimate.Should().Be(Estimate("opus", 20));
    }

    /// <summary>
    ///     Two estimates close together, such as the turns typed twice: the first one's answer lands after the second's and
    ///     does not replace it.
    ///     Red: number every estimate 1 in EstimateAsync; the older estimate wins.
    /// </summary>
    [Fact]
    public async Task An_older_estimate_does_not_replace_a_newer_one()
    {
        var release = new TaskCompletionSource();
        _handler.On(EstimatePath("sonnet", 10), async () =>
        {
            await release.Task;
            return RecordingHandler.Json(HttpStatusCode.OK, Json(Estimate("sonnet", 10)));
        });
        _handler.OnJson(EstimatePath("sonnet", 20), Json(Estimate("sonnet", 20)));

        var first = _effects.EstimateAsync(new CostEstimator("sonnet", 10, 4000), CancellationToken.None);
        await _effects.EstimateAsync(new CostEstimator("sonnet", 20, 4000), CancellationToken.None);
        release.SetResult();
        await first;

        _store.Value.Estimate.Should().Be(Estimate("sonnet", 20));
    }

    /// <summary>
    ///     A failed estimate shows none and is logged; one whose token cannot be refreshed leaves the estimate and is not.
    ///     Red: dispatch nothing on a failure in EstimateAsync; the old estimate stays.
    ///     Red: drop the LogEstimateFailed call; no warning is written.
    ///     Red: dispatch CostEstimateFailed for SigningInAgain too; the estimate goes.
    /// </summary>
    [Fact]
    public async Task A_failed_estimate_shows_none_and_a_renewal_changes_nothing()
    {
        _handler.OnJson(EstimatePath("sonnet"), Json(Estimate("sonnet", 10)));
        await _effects.EstimateAsync(new CostEstimator("sonnet", 10, 4000), CancellationToken.None);
        _handler.On(EstimatePath("sonnet", 20), SigningIn).On(EstimatePath("sonnet", 30), ServerError);

        await _effects.EstimateAsync(new CostEstimator("sonnet", 20, 4000), CancellationToken.None);
        var afterRenewal = _store.Value.Estimate;
        var logsAfterRenewal = _logger.Entries.Count;
        await _effects.EstimateAsync(new CostEstimator("sonnet", 30, 4000), CancellationToken.None);

        afterRenewal.Should().Be(Estimate("sonnet", 10));
        logsAfterRenewal.Should().Be(0);
        _store.Value.Estimate.Should().BeNull();
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    ///     An estimate for a page already gone sends nothing and changes nothing.
    ///     Red: drop the cancelled-token guard at the top of EstimateAsync; the inputs change.
    /// </summary>
    [Fact]
    public async Task An_estimate_for_a_page_already_gone_changes_nothing()
    {
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();

        await _effects.EstimateAsync(new CostEstimator("opus", 20, 5000), gone.Token);

        _store.Value.Estimator.Should().Be(CostEstimator.Default);
        _handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     A project's tasks are read once: a second expand reads nothing.
    ///     Red: dispatch nothing on the answer in LoadTaskCostsAsync; the project has no tasks.
    ///     Red: drop the cached check in LoadTaskCostsAsync; the project's path is read twice.
    /// </summary>
    [Fact]
    public async Task A_projects_tasks_are_read_once()
    {
        _handler.OnJson(TasksPath, Json(new[] { new TaskCostDto(Guid.NewGuid(), "Ship", 1, 2, 0.1m, 1, ExcludedCostDto.None, "s") }));

        await _effects.LoadTaskCostsAsync(ProjectId, CancellationToken.None);
        await _effects.LoadTaskCostsAsync(ProjectId, CancellationToken.None);

        _store.Value.TaskCosts[ProjectId].Should().ContainSingle().Which.TaskTitle.Should().Be("Ship");
        _handler.Paths.Should().Equal(TasksPath);
    }

    /// <summary>
    ///     A project whose tasks fail is not cached, so the next expand asks again, and the failure is logged; one whose
    ///     token cannot be refreshed is not logged.
    ///     Red: cache an empty list on a failure in LoadTaskCostsAsync; the next expand reads nothing.
    ///     Red: drop the LogTaskCostsFailed call; no warning is written.
    ///     Red: log the failure before the SigningInAgain check in LoadTaskCostsAsync; two warnings are written.
    /// </summary>
    [Fact]
    public async Task A_failed_projects_tasks_are_asked_for_again()
    {
        _handler.On(TasksPath, ServerError);
        await _effects.LoadTaskCostsAsync(ProjectId, CancellationToken.None);
        _handler.On(TasksPath, SigningIn);
        await _effects.LoadTaskCostsAsync(ProjectId, CancellationToken.None);

        _store.Value.TaskCosts.Should().NotContainKey(ProjectId);
        _handler.Paths.Should().Equal(TasksPath, TasksPath);
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    ///     A project's tasks for a page that closes while they are in flight are not kept.
    ///     Red: drop the cancelled-token guard after the read in LoadTaskCostsAsync; the cancelled read is logged.
    /// </summary>
    [Fact]
    public async Task A_projects_tasks_whose_page_closes_are_not_kept()
    {
        using var closing = new CancellationTokenSource();
        _handler.On(TasksPath, async () =>
        {
            await closing.CancelAsync();
            return RecordingHandler.Json(HttpStatusCode.OK, "[]");
        });

        await _effects.LoadTaskCostsAsync(ProjectId, closing.Token);

        _store.Value.TaskCosts.Should().NotContainKey(ProjectId);
        _logger.Entries.Should().BeEmpty();
    }
}
