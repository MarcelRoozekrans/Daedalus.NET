using System.Data.Async;
using Daedalus.Domain.CodeAnalysis;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;

namespace Daedalus.Infrastructure.Persistence.Orm.CodeAnalysisRequests;

/// <summary>
///     The SQL of the <c>"CodeAnalysisRequests"</c> and <c>"AnalysisIterations"</c> tables, over one open connection. Its
///     public methods take and return <see cref="CodeAnalysisRequest"/>; the generated queries, their companions and the
///     row records stay inside.
/// </summary>
/// <remarks>
///     A request and its iterations read as one command of two statements, a tuple, in place of EF Core's
///     <c>Include</c>; the lists and the reads before a save read the request alone, as EF did. The lifecycle save is
///     guarded by the <c>xmin</c> the request was read with and writes the ten lifecycle columns its domain methods
///     change; the work tree path and the last prompt are independent saves of their own columns by id. The inserts are
///     raw SQL, as the ORM has no LINQ insert (ruling R10), and list every live column; the dead <c>bytea</c>
///     <c>"RowVersion"</c> stays NULL, as EF left it. No write reads the new <c>xmin</c> back (ruling R58).
/// </remarks>
public sealed partial class CodeAnalysisSql(IAsyncDbConnection connection)
{
    private const string InsertRequestSql = """
        INSERT INTO "CodeAnalysisRequests"
            ("Id", "Title", "Description", "RepositoryUrl", "RepositoryBranch", "CommitSha", "Platform", "FilePath",
             "StartLine", "EndLine", "Type", "Priority", "Requirements", "ReferencedDocuments", "ExternalReferences",
             "LocalWorkTreePath", "FeatureBranchName", "Status", "CurrentIteration", "MaxIterations", "CompletionPromise",
             "LastPromptSent", "LastAiResponse", "PullRequestUrl", "CommitShaFinal", "ValidationResult",
             "HasFailedValidation", "CreatedAt", "StartedAt", "CompletedAt", "CancelledAt", "FailureReason", "CreatedBy",
             "AssignedTo")
        VALUES (@id, @title, @description, @repositoryUrl, @repositoryBranch, @commitSha, @platform, @filePath,
                @startLine, @endLine, @type, @priority, @requirements, @referencedDocuments, @externalReferences,
                @localWorkTreePath, @featureBranchName, @status, @currentIteration, @maxIterations, @completionPromise,
                @lastPromptSent, @lastAiResponse, @pullRequestUrl, @commitShaFinal, @validationResult,
                @hasFailedValidation, @createdAt, @startedAt, @completedAt, @cancelledAt, @failureReason, @createdBy,
                @assignedTo)
        """;

    private const string InsertIterationSql = """
        INSERT INTO "AnalysisIterations"
            ("Id", "CodeAnalysisRequestId", "IterationNumber", "BranchName", "CommitSha", "PromptSent", "AiResponse",
             "CompilationSucceeded", "TestsPassed", "ValidationErrors", "CreatedAt", "InputTokens", "ModelId",
             "OutputTokens")
        VALUES (@id, @codeAnalysisRequestId, @iterationNumber, @branchName, @commitSha, @promptSent, @aiResponse,
                @compilationSucceeded, @testsPassed, @validationErrors, @createdAt, @inputTokens, @modelId,
                @outputTokens)
        """;

    /// <summary>
    ///     Inserts <paramref name="request"/>'s row with every live column as given, and its iterations, in one
    ///     transaction, as EF's one <c>SaveChanges</c> did: none is stored without the others.
    /// </summary>
    /// <param name="request">The request to store.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>A task that completes when the transaction is committed.</returns>
    public async Task InsertAsync(CodeAnalysisRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await InsertRequest(
            request.Id, request.Title, request.Description, request.Repository.Url, request.Repository.Branch,
            request.Repository.CommitSha, (int)request.Repository.Platform, request.Location.FilePath,
            request.Location.StartLine, request.Location.EndLine, (int)request.Type, request.Priority,
            request.Requirements, request.ReferencedDocuments, request.ExternalReferences, request.LocalWorkTreePath,
            request.FeatureBranchName, (int)request.Status, request.CurrentIteration, request.MaxIterations,
            request.CompletionPromise, request.LastPromptSent, request.LastAiResponse, request.Outcome.PullRequestUrl,
            request.Outcome.CommitShaFinal, request.Outcome.ValidationResult, request.Outcome.HasFailedValidation,
            request.CreatedAt, request.StartedAt, request.CompletedAt, request.CancelledAt, request.FailureReason,
            request.CreatedBy, request.AssignedTo, tx, ct).ConfigureAwait(false);
        foreach (var iteration in request.Iterations)
        {
            await InsertIterationAsync(iteration, tx, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the request with <paramref name="id"/> and its iterations, in one round trip.</summary>
    /// <param name="id">The request id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    ///     The request as stored with its row version and its iterations, by iteration number and then by id; or null
    ///     when there is none.
    /// </returns>
    public async Task<CodeAnalysisRequest?> FindWithIterationsAsync(Guid id, CancellationToken ct)
    {
        var (head, iterations) = await FindWithIterationsRows(id, ct).ConfigureAwait(false);
        return head?.ToDomain(iterations.ConvertAll(i => i.ToDomain()));
    }

    /// <summary>
    ///     Reads the request with <paramref name="id"/> without its iterations, as EF's reads before a save did.
    /// </summary>
    /// <param name="id">The request id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The request as stored with its row version; or null when there is none.</returns>
    public async Task<CodeAnalysisRequest?> FindAsync(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct).ConfigureAwait(false);
        return row?.ToDomain([]);
    }

    /// <summary>
    ///     Reads at most <paramref name="batchSize"/> pending or ready requests, by priority, then oldest first, then by
    ///     id, without their iterations.
    /// </summary>
    /// <param name="batchSize">The most requests to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The requests as stored.</returns>
    public async Task<IReadOnlyList<CodeAnalysisRequest>> ListPendingAsync(int batchSize, CancellationToken ct)
    {
        var rows = await ListPending(batchSize, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain([]));
    }

    /// <summary>Reads the requests with <paramref name="status"/>, newest first, without their iterations.</summary>
    /// <param name="status">The status.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The requests as stored, ordered by <c>CreatedAt</c> then id, both descending.</returns>
    public async Task<IReadOnlyList<CodeAnalysisRequest>> ListByStatusAsync(AnalysisStatus status, CancellationToken ct)
    {
        var rows = await ListByStatus(status, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain([]));
    }

    /// <summary>
    ///     Reads the requests whose repository URL is <paramref name="repository"/>, compared ordinally, newest first,
    ///     without their iterations.
    /// </summary>
    /// <param name="repository">The repository URL, as stored.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The requests as stored, ordered by <c>CreatedAt</c> then id, both descending.</returns>
    public async Task<IReadOnlyList<CodeAnalysisRequest>> ListByRepositoryAsync(string repository, CancellationToken ct)
    {
        var rows = await ListByRepository(repository, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain([]));
    }

    /// <summary>Whether a request with <paramref name="id"/> is stored.</summary>
    /// <param name="id">The request id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>True when the row exists.</returns>
    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Exists(id, ct);

    /// <summary>
    ///     Writes <paramref name="request"/>'s lifecycle columns, the status, the iteration count, the start, completion
    ///     and cancellation times, the failure reason and the outcome's four columns, when the row still has the
    ///     <c>xmin</c> the request was read with. No other column is written.
    /// </summary>
    /// <param name="request">The request as changed, carrying the row version it was read with.</param>
    /// <param name="ct">Cancels the save.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">The row was changed or deleted since the request was read.</exception>
    public async Task SaveLifecycleAsync(CodeAnalysisRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await SaveLifecycleAsync(request, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Saves an iteration in one transaction: first <paramref name="request"/>'s lifecycle columns, as
    ///     <see cref="SaveLifecycleAsync(CodeAnalysisRequest, CancellationToken)"/> does, guarded by the <c>xmin</c> it
    ///     was read with; then <paramref name="iteration"/>'s row. A failure of either write rolls both back.
    /// </summary>
    /// <param name="request">The request as changed by recording the iteration.</param>
    /// <param name="iteration">The iteration to insert.</param>
    /// <param name="ct">Cancels the save.</param>
    /// <returns>A task that completes when the transaction is committed.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">The row was changed or deleted since the request was read.</exception>
    public async Task SaveIterationAsync(CodeAnalysisRequest request, AnalysisIteration iteration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(iteration);

        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await SaveLifecycleAsync(request, tx, ct).ConfigureAwait(false);
        await InsertIterationAsync(iteration, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Writes the work tree path of the request with <paramref name="id"/>, by id alone, no version check.</summary>
    /// <param name="id">The request id.</param>
    /// <param name="localWorkTreePath">The path to store.</param>
    /// <param name="ct">Cancels the save.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">No row has the id.</exception>
    public Task SetWorkTreePathAsync(Guid id, string? localWorkTreePath, CancellationToken ct) =>
        SetWorkTreePath(id, localWorkTreePath, ct);

    /// <summary>
    ///     Writes the last prompt and response of the request with <paramref name="id"/>, by id alone, no version check.
    /// </summary>
    /// <param name="id">The request id.</param>
    /// <param name="lastPromptSent">The prompt to store.</param>
    /// <param name="lastAiResponse">The response to store.</param>
    /// <param name="ct">Cancels the save.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">No row has the id.</exception>
    public Task SetLastPromptAsync(Guid id, string? lastPromptSent, string? lastAiResponse, CancellationToken ct) =>
        SetLastPrompt(id, lastPromptSent, lastAiResponse, ct);

    private Task SaveLifecycleAsync(CodeAnalysisRequest request, IAsyncDbTransaction tx, CancellationToken ct)
    {
        var outcome = request.Outcome;
        return SaveLifecycle(
            request.Id, request.RowVersion, request.Status, request.CurrentIteration, request.StartedAt,
            request.CompletedAt, request.CancelledAt, request.FailureReason, outcome.PullRequestUrl,
            outcome.CommitShaFinal, outcome.ValidationResult, outcome.HasFailedValidation, tx, ct);
    }

    private Task<int> InsertIterationAsync(AnalysisIteration i, IAsyncDbTransaction tx, CancellationToken ct) =>
        InsertIteration(
            i.Id, i.CodeAnalysisRequestId, i.IterationNumber, i.BranchName, i.CommitSha, i.PromptSent, i.AiResponse,
            i.CompilationSucceeded, i.TestsPassed, i.ValidationErrors, i.CreatedAt, i.InputTokens, i.ModelId,
            i.OutputTokens, tx, ct);

    [Command(InsertRequestSql)]
    private partial Task<int> InsertRequest(
        Guid id, string title, string description, string repositoryUrl, string? repositoryBranch, string? commitSha,
        int platform, string filePath, int? startLine, int? endLine, int type, int priority, [Json] string requirements,
        [Json] string? referencedDocuments, [Json] string? externalReferences, string? localWorkTreePath,
        string? featureBranchName, int status, int currentIteration, int maxIterations, string? completionPromise,
        string? lastPromptSent, string? lastAiResponse, string? pullRequestUrl, string? commitShaFinal,
        [Json] string? validationResult, bool hasFailedValidation, DateTime createdAt, DateTime? startedAt,
        DateTime? completedAt, DateTime? cancelledAt, string? failureReason, Guid? createdBy, Guid? assignedTo,
        IAsyncDbTransaction tx, CancellationToken ct);

    [Command(InsertIterationSql)]
    private partial Task<int> InsertIteration(
        Guid id, Guid codeAnalysisRequestId, int iterationNumber, string? branchName, string? commitSha,
        string promptSent, string aiResponse, bool? compilationSucceeded, bool? testsPassed,
        [Json] string? validationErrors, DateTime createdAt, int inputTokens, string? modelId, int outputTokens,
        IAsyncDbTransaction tx, CancellationToken ct);

    [LinqQuery(nameof(FindWithIterationsRowsQuery))]
    private partial Task<(CodeAnalysisRequestRow? Head, List<AnalysisIterationRow> Iterations)> FindWithIterationsRows(
        Guid id, CancellationToken ct);

    private static (IQuery<CodeAnalysisRequestRow>, IQuery<AnalysisIterationRow>) FindWithIterationsRowsQuery(
        Guid id) =>
        (Sql.Table<CodeAnalysisRequest>()
            .Where(r => r.Id == id)
            .Select(r => new CodeAnalysisRequestRow(
                r.Id, r.Title, r.Description, r.Repository.Url, r.Repository.Branch, r.Repository.CommitSha,
                r.Repository.Platform, r.Location.FilePath, r.Location.StartLine, r.Location.EndLine, r.Type, r.Priority,
                r.Requirements, r.ReferencedDocuments, r.ExternalReferences, r.LocalWorkTreePath, r.FeatureBranchName,
                r.Status, r.CurrentIteration, r.MaxIterations, r.CompletionPromise, r.LastPromptSent, r.LastAiResponse,
                r.Outcome.PullRequestUrl, r.Outcome.CommitShaFinal, r.Outcome.ValidationResult,
                r.Outcome.HasFailedValidation, r.CreatedAt, r.StartedAt, r.CompletedAt, r.CancelledAt, r.FailureReason,
                r.CreatedBy, r.AssignedTo, r.RowVersion)),
         Sql.Table<AnalysisIteration>()
            .Where(i => i.CodeAnalysisRequestId == id)
            .OrderBy(i => i.IterationNumber)
            .ThenBy(i => i.Id)
            .Select(i => new AnalysisIterationRow(
                i.Id, i.CodeAnalysisRequestId, i.IterationNumber, i.BranchName, i.CommitSha, i.PromptSent,
                i.AiResponse, i.CompilationSucceeded, i.TestsPassed, i.ValidationErrors, i.InputTokens,
                i.OutputTokens, i.ModelId, i.CreatedAt)));

    [LinqQuery(nameof(FindQuery))]
    private partial Task<CodeAnalysisRequestRow?> Find(Guid id, CancellationToken ct);

    private static IQuery<CodeAnalysisRequestRow> FindQuery(Guid id) =>
        Sql.Table<CodeAnalysisRequest>()
            .Where(r => r.Id == id)
            .Select(r => new CodeAnalysisRequestRow(
                r.Id, r.Title, r.Description, r.Repository.Url, r.Repository.Branch, r.Repository.CommitSha,
                r.Repository.Platform, r.Location.FilePath, r.Location.StartLine, r.Location.EndLine, r.Type, r.Priority,
                r.Requirements, r.ReferencedDocuments, r.ExternalReferences, r.LocalWorkTreePath, r.FeatureBranchName,
                r.Status, r.CurrentIteration, r.MaxIterations, r.CompletionPromise, r.LastPromptSent, r.LastAiResponse,
                r.Outcome.PullRequestUrl, r.Outcome.CommitShaFinal, r.Outcome.ValidationResult,
                r.Outcome.HasFailedValidation, r.CreatedAt, r.StartedAt, r.CompletedAt, r.CancelledAt, r.FailureReason,
                r.CreatedBy, r.AssignedTo, r.RowVersion));

    [LinqQuery(nameof(ListPendingQuery))]
    private partial Task<List<CodeAnalysisRequestRow>> ListPending(int batchSize, CancellationToken ct);

    private static IQuery<CodeAnalysisRequestRow> ListPendingQuery(int batchSize) =>
        Sql.Table<CodeAnalysisRequest>()
            .Where(r => r.Status == AnalysisStatus.Pending || r.Status == AnalysisStatus.Ready)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Take(batchSize)
            .Select(r => new CodeAnalysisRequestRow(
                r.Id, r.Title, r.Description, r.Repository.Url, r.Repository.Branch, r.Repository.CommitSha,
                r.Repository.Platform, r.Location.FilePath, r.Location.StartLine, r.Location.EndLine, r.Type, r.Priority,
                r.Requirements, r.ReferencedDocuments, r.ExternalReferences, r.LocalWorkTreePath, r.FeatureBranchName,
                r.Status, r.CurrentIteration, r.MaxIterations, r.CompletionPromise, r.LastPromptSent, r.LastAiResponse,
                r.Outcome.PullRequestUrl, r.Outcome.CommitShaFinal, r.Outcome.ValidationResult,
                r.Outcome.HasFailedValidation, r.CreatedAt, r.StartedAt, r.CompletedAt, r.CancelledAt, r.FailureReason,
                r.CreatedBy, r.AssignedTo, r.RowVersion));

    [LinqQuery(nameof(ListByStatusQuery))]
    private partial Task<List<CodeAnalysisRequestRow>> ListByStatus(AnalysisStatus status, CancellationToken ct);

    private static IQuery<CodeAnalysisRequestRow> ListByStatusQuery(AnalysisStatus status) =>
        Sql.Table<CodeAnalysisRequest>()
            .Where(r => r.Status == status)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => new CodeAnalysisRequestRow(
                r.Id, r.Title, r.Description, r.Repository.Url, r.Repository.Branch, r.Repository.CommitSha,
                r.Repository.Platform, r.Location.FilePath, r.Location.StartLine, r.Location.EndLine, r.Type, r.Priority,
                r.Requirements, r.ReferencedDocuments, r.ExternalReferences, r.LocalWorkTreePath, r.FeatureBranchName,
                r.Status, r.CurrentIteration, r.MaxIterations, r.CompletionPromise, r.LastPromptSent, r.LastAiResponse,
                r.Outcome.PullRequestUrl, r.Outcome.CommitShaFinal, r.Outcome.ValidationResult,
                r.Outcome.HasFailedValidation, r.CreatedAt, r.StartedAt, r.CompletedAt, r.CancelledAt, r.FailureReason,
                r.CreatedBy, r.AssignedTo, r.RowVersion));

    [LinqQuery(nameof(ListByRepositoryQuery))]
    private partial Task<List<CodeAnalysisRequestRow>> ListByRepository(string repositoryUrl, CancellationToken ct);

    private static IQuery<CodeAnalysisRequestRow> ListByRepositoryQuery(string repositoryUrl) =>
        Sql.Table<CodeAnalysisRequest>()
            .Where(r => string.Equals(r.Repository.Url, repositoryUrl, StringComparison.Ordinal))
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => new CodeAnalysisRequestRow(
                r.Id, r.Title, r.Description, r.Repository.Url, r.Repository.Branch, r.Repository.CommitSha,
                r.Repository.Platform, r.Location.FilePath, r.Location.StartLine, r.Location.EndLine, r.Type, r.Priority,
                r.Requirements, r.ReferencedDocuments, r.ExternalReferences, r.LocalWorkTreePath, r.FeatureBranchName,
                r.Status, r.CurrentIteration, r.MaxIterations, r.CompletionPromise, r.LastPromptSent, r.LastAiResponse,
                r.Outcome.PullRequestUrl, r.Outcome.CommitShaFinal, r.Outcome.ValidationResult,
                r.Outcome.HasFailedValidation, r.CreatedAt, r.StartedAt, r.CompletedAt, r.CancelledAt, r.FailureReason,
                r.CreatedBy, r.AssignedTo, r.RowVersion));

    [LinqQuery(nameof(ExistsQuery))]
    private partial Task<bool> Exists(Guid id, CancellationToken ct);

    private static IQuery<bool> ExistsQuery(Guid id) =>
        Sql.Table<CodeAnalysisRequest>().Where(r => r.Id == id).Any();

    [LinqCommand(nameof(SaveLifecycleQuery), ThrowOnNoRows = true)]
    private partial Task SaveLifecycle(
        Guid id, uint rowVersion, AnalysisStatus status, int currentIteration, DateTime? startedAt,
        DateTime? completedAt, DateTime? cancelledAt, string? failureReason, string? pullRequestUrl,
        string? commitShaFinal, string? validationResult, bool hasFailedValidation, IAsyncDbTransaction tx,
        CancellationToken ct);

    private static int SaveLifecycleQuery(
        Guid id, uint rowVersion, AnalysisStatus status, int currentIteration, DateTime? startedAt,
        DateTime? completedAt, DateTime? cancelledAt, string? failureReason, string? pullRequestUrl,
        string? commitShaFinal, string? validationResult, bool hasFailedValidation) =>
        Sql.Table<CodeAnalysisRequest>()
            .Where(r => r.Id == id && r.RowVersion == rowVersion)
            .ExecuteUpdate(set => set
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.CurrentIteration, currentIteration)
                .SetProperty(r => r.StartedAt, startedAt)
                .SetProperty(r => r.CompletedAt, completedAt)
                .SetProperty(r => r.CancelledAt, cancelledAt)
                .SetProperty(r => r.FailureReason, failureReason)
                .SetProperty(r => r.Outcome.PullRequestUrl, pullRequestUrl)
                .SetProperty(r => r.Outcome.CommitShaFinal, commitShaFinal)
                .SetProperty(r => r.Outcome.ValidationResult, validationResult)
                .SetProperty(r => r.Outcome.HasFailedValidation, hasFailedValidation));

    [LinqCommand(nameof(SetWorkTreePathQuery), ThrowOnNoRows = true)]
    private partial Task SetWorkTreePath(Guid id, string? localWorkTreePath, CancellationToken ct);

    private static int SetWorkTreePathQuery(Guid id, string? localWorkTreePath) =>
        Sql.Table<CodeAnalysisRequest>()
            .Where(r => r.Id == id)
            .ExecuteUpdate(set => set.SetProperty(r => r.LocalWorkTreePath, localWorkTreePath));

    [LinqCommand(nameof(SetLastPromptQuery), ThrowOnNoRows = true)]
    private partial Task SetLastPrompt(
        Guid id, string? lastPromptSent, string? lastAiResponse, CancellationToken ct);

    private static int SetLastPromptQuery(Guid id, string? lastPromptSent, string? lastAiResponse) =>
        Sql.Table<CodeAnalysisRequest>()
            .Where(r => r.Id == id)
            .ExecuteUpdate(set => set
                .SetProperty(r => r.LastPromptSent, lastPromptSent)
                .SetProperty(r => r.LastAiResponse, lastAiResponse));
}
