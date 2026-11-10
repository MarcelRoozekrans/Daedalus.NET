using Daedalus.Domain.CodeAnalysis;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.CodeAnalysisRequests;

/// <summary>
///     Maps <see cref="AnalysisIteration"/> to <c>"AnalysisIterations"</c>. Every column carries its property's name, as
///     EF Core stored it, and <c>"ValidationErrors"</c> is <c>jsonb</c>.
/// </summary>
[TableMap(typeof(AnalysisIteration), "AnalysisIterations")]
[ColumnMap(nameof(AnalysisIteration.ValidationErrors), Json = true)]
internal static class AnalysisIterationMap;
