using Daedalus.Domain.CodeAnalysis;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.CodeAnalysisRequests;

/// <summary>
///     Maps <see cref="CodeAnalysisRequest"/> to <c>"CodeAnalysisRequests"</c>, with the column names EF Core gave it.
///     The owned <see cref="CodeAnalysisRequest.Repository"/>, <see cref="CodeAnalysisRequest.Location"/> and
///     <see cref="CodeAnalysisRequest.Outcome"/> are columns of the same table under their bare member names, such as
///     <c>"RepositoryUrl"</c>, <c>"FilePath"</c> and <c>"PullRequestUrl"</c>. The enums are stored as their integers, as
///     EF Core stored them. <c>"Requirements"</c>, <c>"ReferencedDocuments"</c>, <c>"ExternalReferences"</c> and
///     <c>"ValidationResult"</c> are <c>jsonb</c>. <see cref="CodeAnalysisRequest.RowVersion"/> is PostgreSQL's
///     <c>xmin</c>, the concurrency token of the guarded saves, not the dead <c>bytea</c> <c>"RowVersion"</c> column,
///     which migration 2 dropped. <see cref="CodeAnalysisRequest.Iterations"/> is not a column: the iterations are rows of
///     <c>"AnalysisIterations"</c>.
/// </summary>
[TableMap(typeof(CodeAnalysisRequest), "CodeAnalysisRequests")]
[OwnedMap(nameof(CodeAnalysisRequest.Repository))]
[OwnedMap(nameof(CodeAnalysisRequest.Location))]
[OwnedMap(nameof(CodeAnalysisRequest.Outcome))]
[ColumnMap("Repository.Url", Column = "RepositoryUrl")]
[ColumnMap("Repository.Branch", Column = "RepositoryBranch")]
[ColumnMap("Repository.CommitSha", Column = "CommitSha")]
[ColumnMap("Repository.Platform", Column = "Platform")]
[ColumnMap("Location.FilePath", Column = "FilePath")]
[ColumnMap("Location.StartLine", Column = "StartLine")]
[ColumnMap("Location.EndLine", Column = "EndLine")]
[ColumnMap("Outcome.PullRequestUrl", Column = "PullRequestUrl")]
[ColumnMap("Outcome.CommitShaFinal", Column = "CommitShaFinal")]
[ColumnMap("Outcome.ValidationResult", Column = "ValidationResult", Json = true)]
[ColumnMap("Outcome.HasFailedValidation", Column = "HasFailedValidation")]
[ColumnMap(nameof(CodeAnalysisRequest.Requirements), Json = true)]
[ColumnMap(nameof(CodeAnalysisRequest.ReferencedDocuments), Json = true)]
[ColumnMap(nameof(CodeAnalysisRequest.ExternalReferences), Json = true)]
[ColumnMap(nameof(CodeAnalysisRequest.RowVersion), Column = "xmin")]
internal static class CodeAnalysisRequestMap;
