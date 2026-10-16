using System.Collections.Immutable;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Costs;

/// <summary>What the Costs page's estimator asks for: a model, a number of turns and the prompt tokens per turn.</summary>
/// <param name="ModelId">The model to price the turns at.</param>
/// <param name="Turns">How many turns.</param>
/// <param name="PromptTokens">The estimated prompt tokens per turn.</param>
public readonly record struct CostEstimator(string ModelId, int Turns, int PromptTokens)
{
    /// <summary>
    ///     What the estimator starts on before the pricing lands: no model, which the pricing then supplies with the model
    ///     the API calls, listed first.
    /// </summary>
    public static CostEstimator Default { get; } = new("", 10, 4000);
}

/// <summary>
///     The Costs page: the summary, the per-project history, the pricing, the estimator's inputs and estimate, and each
///     expanded project's tasks. The page has no period filter: it shows totals over everything recorded.
///     <para>
///         Lifetime: page-only, cleared by <see cref="CostsClosed"/> when the page is disposed. Only that page reads or
///         loads it, so its clear races no other reader. <paramref name="Sequence"/> and
///         <paramref name="EstimateSequence"/> survive the clear, so an answer to a read made before it never lands in a
///         read made after it.
///     </para>
/// </summary>
/// <param name="Summary">The summary, or <see langword="null"/> before it loaded and when it failed.</param>
/// <param name="Projects">The task loop history by project; empty before it loaded and when it failed.</param>
/// <param name="Pricing">The priced models; empty before they loaded and when they failed.</param>
/// <param name="Error">
///     Why the summary failed to load, or <see langword="null"/>. Never <see cref="Auth.SessionExpiry.SigningInAgain"/>,
///     and never a cancellation: neither is stored. Only the summary's failure shows; the tables show empty.
/// </param>
/// <param name="Estimator">What the estimator asks for; no model until the pricing lands, then the first priced model.</param>
/// <param name="Estimate">The newest estimate, or <see langword="null"/> before one and after one failed.</param>
/// <param name="TaskCosts">Each expanded project's tasks, by project id; a project is read once.</param>
/// <param name="Sequence">The number of the newest page read, never reset; an answer to an older one is dropped.</param>
/// <param name="EstimateSequence">The number of the newest estimate, never reset; an older answer is dropped.</param>
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct CostsState(
    CostSummaryDto? Summary,
    IReadOnlyList<ProjectCostDto> Projects,
    IReadOnlyList<ModelPricingDto> Pricing,
    string? Error,
    CostEstimator Estimator,
    CostEstimateDto? Estimate,
    ImmutableDictionary<Guid, IReadOnlyList<TaskCostDto>> TaskCosts,
    long Sequence,
    long EstimateSequence)
{
    /// <summary>The page with nothing read, numbered from the start.</summary>
    public static CostsState Empty { get; } =
        new(null, [], [], null, CostEstimator.Default, null, ImmutableDictionary<Guid, IReadOnlyList<TaskCostDto>>.Empty, 0, 0);

    /// <summary>The state before any read.</summary>
    public static CostsState Initial(IServiceProvider services) => Empty;
}
