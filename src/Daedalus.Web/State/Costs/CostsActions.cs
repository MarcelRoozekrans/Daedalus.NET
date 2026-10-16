namespace Daedalus.Web.State.Costs;

/// <summary>A read of the page's summary, projects and pricing was started, numbered <paramref name="Sequence"/>.</summary>
public readonly record struct CostsLoadStarted(long Sequence);

/// <summary>
///     Read <paramref name="Sequence"/> answered: the <paramref name="Summary"/>, or <see langword="null"/> with the
///     <paramref name="Error"/> to show, or with none when the session is being renewed; the
///     <paramref name="Projects"/> and the <paramref name="Pricing"/>, empty when theirs failed.
/// </summary>
public readonly record struct CostsLoaded(
    long Sequence, CostSummaryDto? Summary, IReadOnlyList<ProjectCostDto> Projects,
    IReadOnlyList<ModelPricingDto> Pricing, string? Error);

/// <summary>An estimate for <paramref name="Estimator"/> was asked for, numbered <paramref name="Sequence"/>.</summary>
public readonly record struct CostEstimateStarted(long Sequence, CostEstimator Estimator);

/// <summary>Estimate <paramref name="Sequence"/> answered with <paramref name="Estimate"/>.</summary>
public readonly record struct CostEstimated(long Sequence, CostEstimateDto Estimate);

/// <summary>Estimate <paramref name="Sequence"/> failed: the page shows no estimate.</summary>
public readonly record struct CostEstimateFailed(long Sequence);

/// <summary>Project <paramref name="ProjectId"/>'s tasks were read.</summary>
public readonly record struct CostTaskCostsLoaded(Guid ProjectId, IReadOnlyList<TaskCostDto> Tasks);

/// <summary>The Costs page closed: what it read goes.</summary>
public readonly record struct CostsClosed;
