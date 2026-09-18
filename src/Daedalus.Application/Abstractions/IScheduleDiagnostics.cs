using Daedalus.Application.DTOs.Scheduling;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Queries the diagnostic state of scheduled runs: retrieving the current state of every schedule
///     and the historical execution details of a single schedule's runs.
/// </summary>
public interface IScheduleDiagnostics
{
    /// <summary>
    ///     Gets a summary of every schedule's current state and the verdict on each run in progress. A disabled
    ///     schedule is included, not excluded, and reports <see cref="RunVerdict.Disabled"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An overview of every schedule and its current run.</returns>
    ValueTask<IReadOnlyList<RunDiagnosis>> GetOverviewAsync(CancellationToken ct);

    /// <summary>Gets the execution history for a single schedule, with the most recent runs first.</summary>
    /// <param name="scheduleId">The schedule to query.</param>
    /// <param name="take">The maximum number of runs to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The most recent executions of the given schedule, ordered by occurrence descending.</returns>
    ValueTask<IReadOnlyList<RunDiagnosis>> GetRunHistoryAsync(Guid scheduleId, int take, CancellationToken ct);
}
