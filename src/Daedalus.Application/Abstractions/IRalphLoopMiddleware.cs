using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     A single stage in the Ralph loop iteration pipeline.
///     Each middleware processes the iteration context and invokes the next stage.
/// </summary>
public interface IRalphLoopMiddleware
{
    /// <summary>
    ///     Order in which this middleware runs (lower = earlier).
    /// </summary>
    int Order { get; }

    /// <summary>
    ///     Process this iteration stage, then call continuation() to continue the pipeline.
    ///     Middleware can short-circuit by not calling continuation().
    /// </summary>
    Task<Result> InvokeAsync(
        RalphIterationContext context,
        Func<Task<Result>> continuation,
        CancellationToken ct);
}
