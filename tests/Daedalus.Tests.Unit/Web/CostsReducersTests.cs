using Daedalus.Application.DTOs;
using Daedalus.Web.State.Costs;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The Costs reducers: what a read keeps, the estimator, the task cache, the sequence guards and the close.</summary>
public sealed class CostsReducersTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static readonly CostSummaryDto Summary = new(
        1, 2, 3, 0, 1.5m, 4, 0, ExcludedCostDto.None, [], [], [], "scope");

    private static readonly ProjectCostDto Project = new(ProjectId, "Widgets", 1, 2, 0.5m, 3, ExcludedCostDto.None, "scope");

    private static readonly ModelPricingDto Sonnet = new("sonnet", "Sonnet", 3m, 15m, 0.3m, 3.75m);
    private static readonly ModelPricingDto Opus = new("opus", "Opus", 15m, 75m, 1.5m, 18.75m);

    private static readonly CostEstimateDto Estimate = new("sonnet", "Sonnet", 10, 4000, 1234, 0.3m, 1m);

    private static readonly TaskCostDto Task = new(Guid.NewGuid(), "Ship", 1, 2, 0.1m, 1, ExcludedCostDto.None, "scope");

    private static CostsState Initial => CostsState.Initial(null!);

    /// <summary>
    ///     The page starts on the estimator's defaults: no model until the pricing names one, 10 turns, 4,000 prompt tokens.
    ///     #385: it started on a retired model ID it named itself.
    ///     Red: start CostEstimator.Default on 5 turns; the turns read 5.
    ///     Red: start CostEstimator.Default on a model ID; the model is that ID.
    /// </summary>
    [Fact]
    public void The_estimator_starts_on_its_defaults()
    {
        Initial.Estimator.Should().Be(new CostEstimator("", 10, 4000));
    }

    /// <summary>
    ///     A read keeps the summary, the projects and the pricing, and the estimator moves to the first priced model.
    ///     Red: drop Summary in the CostsLoaded reducer; the summary stays null.
    ///     Red: drop Projects in the CostsLoaded reducer; the projects stay empty.
    ///     Red: drop Pricing in the CostsLoaded reducer; the pricing stays empty.
    ///     Red: keep the estimator's model in the CostsLoaded reducer; it stays on the default model.
    /// </summary>
    [Fact]
    public void A_read_keeps_what_it_read_and_selects_the_first_priced_model()
    {
        var started = CostsReducers.On(Initial, new CostsLoadStarted(1));

        var state = CostsReducers.On(started, new CostsLoaded(1, Summary, [Project], [Sonnet, Opus], null));

        state.Summary.Should().Be(Summary);
        state.Projects.Should().Equal(Project);
        state.Pricing.Should().Equal(Sonnet, Opus);
        state.Estimator.Should().Be(CostEstimator.Default with { ModelId = "sonnet" });
    }

    /// <summary>
    ///     With no pricing the estimator keeps its model, as the page always did.
    ///     Red: select Pricing.FirstOrDefault()?.ModelId unguarded in the CostsLoaded reducer; the model is null.
    /// </summary>
    [Fact]
    public void No_pricing_keeps_the_estimators_model()
    {
        var state = CostsReducers.On(CostsReducers.On(Initial, new CostsLoadStarted(1)), new CostsLoaded(1, Summary, [], [], null));

        state.Estimator.ModelId.Should().Be(CostEstimator.Default.ModelId);
    }

    /// <summary>
    ///     A failed summary is the error, and the rest still lands.
    ///     Red: drop Error in the CostsLoaded reducer; the error stays null.
    /// </summary>
    [Fact]
    public void A_failed_summary_is_the_error()
    {
        var state = CostsReducers.On(
            CostsReducers.On(Initial, new CostsLoadStarted(1)), new CostsLoaded(1, null, [Project], [Sonnet], "API error 500"));

        state.Error.Should().Be("API error 500");
        state.Pricing.Should().Equal(Sonnet);
    }

    /// <summary>
    ///     A start clears the last error and takes the newest number, never moving it back.
    ///     Red: keep Error in the CostsLoadStarted reducer; the old error is still there.
    ///     Red: leave Sequence unset in the CostsLoadStarted reducer; the sequence stays 3.
    ///     Red: set Sequence to the action's in the CostsLoadStarted reducer; an out-of-order start moves it back to 2.
    /// </summary>
    [Fact]
    public void A_start_clears_the_error_and_takes_the_newest_number()
    {
        var failed = Initial with { Error = "boom", Sequence = 3 };

        var started = CostsReducers.On(failed, new CostsLoadStarted(4));

        started.Error.Should().BeNull();
        started.Sequence.Should().Be(4);
        CostsReducers.On(started, new CostsLoadStarted(2)).Sequence.Should().Be(4);
    }

    /// <summary>
    ///     An answer to an older read is dropped.
    ///     Red: drop the sequence check in the CostsLoaded reducer; the older summary lands.
    /// </summary>
    [Fact]
    public void An_older_read_is_dropped()
    {
        var state = CostsReducers.On(Initial with { Sequence = 2 }, new CostsLoaded(1, Summary, [], [], null));

        state.Summary.Should().BeNull();
    }

    /// <summary>
    ///     An estimate's start takes its inputs and keeps the estimate on show until the new one lands; the new one then
    ///     replaces it.
    ///     Red: keep Estimator in the CostEstimateStarted reducer; the turns stay 10.
    ///     Red: clear Estimate in the CostEstimateStarted reducer; the old estimate goes before the new one lands.
    ///     Red: drop Estimate in the CostEstimated reducer; the new estimate never lands.
    /// </summary>
    [Fact]
    public void An_estimate_takes_its_inputs_and_lands()
    {
        var shown = Initial with { Estimate = Estimate, EstimateSequence = 1 };
        var newer = Estimate with { MaxIterations = 20 };

        var started = CostsReducers.On(shown, new CostEstimateStarted(2, CostEstimator.Default with { Turns = 20 }));
        var landed = CostsReducers.On(started, new CostEstimated(2, newer));

        started.Estimator.Turns.Should().Be(20);
        started.Estimate.Should().Be(Estimate);
        landed.Estimate.Should().Be(newer);
    }

    /// <summary>
    ///     An older estimate's answer, or its failure, is dropped: the newest inputs' estimate stays.
    ///     Red: drop the sequence check in the CostEstimated reducer; the older estimate lands.
    ///     Red: drop the sequence check in the CostEstimateFailed reducer; the older failure clears the estimate.
    ///     Red: leave EstimateSequence unset in the CostEstimateStarted reducer; the older estimate lands.
    /// </summary>
    [Fact]
    public void An_older_estimate_is_dropped()
    {
        var newer = Estimate with { MaxIterations = 20 };
        var started = CostsReducers.On(Initial, new CostEstimateStarted(1, CostEstimator.Default));
        started = CostsReducers.On(started, new CostEstimateStarted(2, CostEstimator.Default with { Turns = 20 }));
        var landed = CostsReducers.On(started, new CostEstimated(2, newer));

        CostsReducers.On(landed, new CostEstimated(1, Estimate)).Estimate.Should().Be(newer);
        CostsReducers.On(landed, new CostEstimateFailed(1)).Estimate.Should().Be(newer);
    }

    /// <summary>
    ///     A failed estimate shows none, as the page always did.
    ///     Red: keep Estimate in the CostEstimateFailed reducer; the old estimate stays.
    /// </summary>
    [Fact]
    public void A_failed_estimate_shows_none()
    {
        var started = CostsReducers.On(Initial with { Estimate = Estimate }, new CostEstimateStarted(1, CostEstimator.Default));

        CostsReducers.On(started, new CostEstimateFailed(1)).Estimate.Should().BeNull();
    }

    /// <summary>
    ///     A project's tasks are kept by its id.
    ///     Red: drop the add in the CostTaskCostsLoaded reducer; the project has no tasks.
    /// </summary>
    [Fact]
    public void A_projects_tasks_are_kept_by_its_id()
    {
        var state = CostsReducers.On(Initial, new CostTaskCostsLoaded(ProjectId, [Task]));

        state.TaskCosts[ProjectId].Should().Equal(Task);
    }

    /// <summary>
    ///     The close clears what the page read and keeps both numbers, so a late answer never lands in the next page.
    ///     Red: return the state unchanged in the CostsClosed reducer; the summary is still there.
    ///     Red: return CostsState.Empty in the CostsClosed reducer; the sequences go back to 0.
    /// </summary>
    [Fact]
    public void The_close_clears_the_page_and_keeps_the_numbers()
    {
        var read = Initial with
        {
            Summary = Summary,
            TaskCosts = Initial.TaskCosts.Add(ProjectId, [Task]),
            Sequence = 3,
            EstimateSequence = 5,
        };

        var closed = CostsReducers.On(read, new CostsClosed());

        closed.Summary.Should().BeNull();
        closed.TaskCosts.Should().BeEmpty();
        closed.Sequence.Should().Be(3);
        closed.EstimateSequence.Should().Be(5);
    }
}
