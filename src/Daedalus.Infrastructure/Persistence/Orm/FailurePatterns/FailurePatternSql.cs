using System.Data.Async;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;

namespace Daedalus.Infrastructure.Persistence.Orm.FailurePatterns;

/// <summary>
///     The read-only SQL of the failure pattern search over <c>"TaskExecutions"</c>, over one open connection. It reads
///     through the table's map beside <see cref="Tasks.TaskSql"/> and adds only two narrow read shapes.
/// </summary>
/// <remarks>
///     Each error is paired with the next successful iteration of its task by a second query, one per error, as the
///     EF Core version did; the pairing stays an N+1 read, now over one connection.
/// </remarks>
public sealed partial class FailurePatternSql(IAsyncDbConnection connection)
{
    private const int ResolutionLength = 500;

    /// <summary>
    ///     Finds error→fix pairs whose error matches <paramref name="pattern"/>, newest error first.
    /// </summary>
    /// <param name="pattern">The <c>ILIKE</c> pattern, wildcards included.</param>
    /// <param name="errorLimit">How many matching errors to look at.</param>
    /// <param name="maxResults">How many pairs to return at most.</param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>The pairs, in the order of their errors; an error without a later success yields none.</returns>
    public async Task<IReadOnlyList<FailurePatternRecord>> SearchAsync(
        string pattern, int errorLimit, int maxResults, CancellationToken ct)
    {
        var errors = await ErrorExecutions(pattern, errorLimit, ct).ConfigureAwait(false);
        var patterns = new List<FailurePatternRecord>();

        foreach (var error in errors)
        {
            var resolution = await Resolution(error.TaskId, error.IterationNumber, ct).ConfigureAwait(false);
            if (resolution is not null)
            {
                patterns.Add(new FailurePatternRecord
                {
                    ErrorText = error.Error,
                    Resolution = resolution.LlmResponse.Length > ResolutionLength
                        ? resolution.LlmResponse[..ResolutionLength]
                        : resolution.LlmResponse,
                    SourceTaskId = error.TaskId,
                    ErrorIteration = error.IterationNumber,
                    ResolutionIteration = resolution.IterationNumber,
                    ObservedAt = error.ExecutedAt
                });
            }

            if (patterns.Count >= maxResults)
            {
                break;
            }
        }

        return patterns;
    }

    [LinqQuery(nameof(ErrorExecutionsQuery))]
    private partial Task<List<ErrorExecutionRow>> ErrorExecutions(string pattern, int limit, CancellationToken ct);

    private static IQuery<ErrorExecutionRow> ErrorExecutionsQuery(string pattern, int limit) =>
        Sql.Table<TaskExecution>()
            .Where(e => e.Error != null && Sql.ILike(e.Error, pattern))
            .OrderByDescending(e => e.ExecutedAt)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .Select(e => new ErrorExecutionRow(e.TaskId, e.IterationNumber, e.Error!, e.ExecutedAt));

    // Raw, not a LinqQuery: the LINQ chain has no string length or IsNullOrEmpty yet, and the non-empty response is
    // the point of the lookup. Swap back to a LinqQuery once ZeroAlloc.ORM issue 453 ships (ruling R64).
    [Query("""
        SELECT "IterationNumber", "LlmResponse"
        FROM "TaskExecutions"
        WHERE "TaskId" = @taskId AND "IterationNumber" > @afterIteration AND "Error" IS NULL
              AND length("LlmResponse") > 0
        ORDER BY "IterationNumber", "Id"
        LIMIT 1
        """)]
    private partial Task<ResolutionExecutionRow?> Resolution(Guid taskId, int afterIteration, CancellationToken ct);
}
