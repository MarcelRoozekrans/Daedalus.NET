using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The real <see cref="IManufactureRunStarter"/>, registered by
///     <c>DaedalusAgentsServiceCollectionExtensions.AddDaedalusWorkflow</c> in place of
///     <see cref="DisabledManufactureRunStarter"/> whenever <c>Thalos:Workflow:Enabled</c> is
///     <see langword="true"/>. Resolves the request's repository against <see cref="WorkflowConfig.Repositories"/>,
///     prepares the run's git worktree through <see cref="IRunWorkspaceProvider"/>, and then shapes one call onto
///     <see cref="WorkflowRunStarter.StartAsync"/> under the same run id: the process is always <c>manufacture</c>,
///     the opening variable is always <c>work_intent</c>, and the work intent and the worktree's standing
///     instructions file are pinned into the manifest as <see cref="WorkIntentDocument"/> and
///     <see cref="StandingInstructionsDocument"/>.
/// </summary>
/// <remarks>
///     <para>
///     <b>The repository is a name, resolved here.</b> A request never carries a remote URL. The only remotes a run
///     can reach are the reviewed entries of <see cref="WorkflowConfig.Repositories"/>, matched by
///     <see cref="RepositoryConfig.Name"/> with an ordinal comparison, and any other name fails the start before a
///     worktree or a run row exists.
///     </para>
///     <para>
///     <b>The worktree is created immediately before the run, under the same id.</b> The workspace sweeper treats a
///     workspace with no run row as an orphan once its grace period has passed since the workspace became ready, so
///     nothing slow sits between the create and the start: only the read of the standing instructions from the new
///     worktree, which the run pins. When the start then reports a failure, no run row was written, so the worktree
///     is removed again. When the start throws instead, whether a row was written is unknown, and the worktree is
///     left for the sweeper, which decides on the run's state rather than on a guess made here.
///     </para>
///     <para>
///     <b>The standing instructions come from the worktree, not from the host.</b> They are read fresh from
///     <c>&lt;worktree&gt;/&lt;StandingInstructionsPath&gt;</c> on every start, so what a run pins is the text on the
///     repository's default branch at the moment it started, or <c>""</c> when the repository has no such file. The
///     path is resolved through <see cref="WorkspacePath.Resolve"/>, as <see cref="StandingInstructionsWriter"/> resolves
///     the file an approved proposal replaces, and a path it refuses fails the start and removes the worktree.
///     </para>
///     <para>
///     <b>The correlation key is the run id, not an idempotency key over the caller's input.</b> Two calls with the
///     same <c>workIntent</c> text always start two different runs, because every call mints a fresh run id. The key
///     space <see cref="IWorkflowStore.StartAsync(WorkflowStartRequest,System.Threading.CancellationToken)"/>
///     enforces uniqueness over is global and permanent for the life of the database, so the prefix exists only to
///     make a run's correlation key human-readable in a query, not to partition it from any other process's keys.
///     </para>
/// </remarks>
public sealed class ManufactureRunStarter(
    WorkflowRunStarter starter, IRunWorkspaceProvider workspaces, IRunBaseFileReader baseFiles, WorkflowConfig config) : IManufactureRunStarter
{
    /// <summary>The manifest document key the worktree's standing instructions file is pinned under.</summary>
    public const string StandingInstructionsDocument = "standing_instructions";

    /// <summary>The manifest document key the request's work intent is pinned under.</summary>
    public const string WorkIntentDocument = "work_intent";

    /// <summary>
    ///     The <see cref="Exception.Data"/> key under which a fault reading the standing instructions carries why its
    ///     worktree could not be removed. Absent when the removal succeeded; the sweeper reclaims a worktree left behind.
    /// </summary>
    public const string WorkspaceRemovalFailureKey = "Daedalus.WorkspaceRemovalFailure";

    /// <summary>The only process this type ever starts.</summary>
    private const string ProcessName = "manufacture";

    /// <summary>
    ///     Chosen to comfortably hold a paragraph or two of intent while still fitting inside a node's rendered
    ///     instruction text — <see cref="WorkflowRun.Variables"/>'s own doc comment notes variables are cut at 512
    ///     characters when rendered, so anything this long would already be truncated well before a node ever saw
    ///     all of it; failing here instead tells the caller before a run is started on a value that would have
    ///     been silently cut.
    /// </summary>
    private const int MaxWorkIntentLength = 4000;

    private readonly WorkflowRunStarter _starter = starter ?? throw new ArgumentNullException(nameof(starter));
    private readonly IRunWorkspaceProvider _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
    private readonly IRunBaseFileReader _baseFiles = baseFiles ?? throw new ArgumentNullException(nameof(baseFiles));
    private readonly WorkflowConfig _config = config ?? throw new ArgumentNullException(nameof(config));

    // Task B11: the same canonical relative path StandingInstructionsWriter writes, so the text a run pins and the
    // file its approved proposal replaces are one file.
    private readonly string _standingInstructionsPath = DaedalusAgentsServiceCollectionExtensions.StandingInstructionsRelativePath(
        (config ?? throw new ArgumentNullException(nameof(config))).StandingInstructionsPath);

    /// <inheritdoc />
    public async ValueTask<Result<Guid, ManufactureStartFailure>> StartAsync(ManufactureStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workIntent = request.WorkIntent;
        if (string.IsNullOrWhiteSpace(workIntent))
        {
            return Fail(ManufactureStartFailureKind.Invalid, "workIntent must not be blank.");
        }

        if (workIntent.Length > MaxWorkIntentLength)
        {
            return Fail(
                ManufactureStartFailureKind.Invalid,
                $"workIntent must be at most {MaxWorkIntentLength} characters, but was {workIntent.Length}.");
        }

        var repository = FindRepository(request.Repository);
        if (repository is null)
        {
            return Fail(ManufactureStartFailureKind.Invalid, $"repository '{request.Repository}' is not allow-listed");
        }

        var runId = Guid.NewGuid();
        var created = await _workspaces.CreateAsync(
            new RunWorkspaceRequest(
                runId, repository.Name, repository.Remote, repository.DefaultBranch, $"manufacture/{runId}", repository.Solution),
            ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return Fail(KindOf(created.Error), $"could not prepare the run's workspace: {created.Error.Message}");
        }

        Result<string?, AgentError> standingInstructions;
        try
        {
            standingInstructions = await _baseFiles.ReadBaseFileAsync(runId, _standingInstructionsPath, ct).ConfigureAwait(false);
        }
        catch (Exception readFault)
        {
            // No run row can exist yet, so the workspace is certainly an orphan: remove it now rather than leave it to
            // the sweeper, then rethrow the read fault itself, unchanged, whatever the removal did.
            await RemoveOrphanAsync(runId, readFault).ConfigureAwait(false);
            throw;
        }

        if (standingInstructions.IsFailure)
        {
            // Refused before any start, so no run row exists: the workspace is removed the same way a reported start
            // failure removes it.
            return await RemoveAfterFailureAsync(
                runId,
                KindOf(standingInstructions.Error),
                $"the standing instructions '{_standingInstructionsPath}' cannot be read from the run's base commit: " +
                standingInstructions.Error.Message).ConfigureAwait(false);
        }

        var variables = new Dictionary<string, object?>(StringComparer.Ordinal) { ["work_intent"] = workIntent };
        var documents = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkIntentDocument] = workIntent,
            // A null value is a file absent at the base commit: the run pins the empty text.
            [StandingInstructionsDocument] = standingInstructions.Value ?? "",
        };

        var started = await _starter.StartAsync(
            new WorkflowRunStartOptions
            {
                Process = ProcessName,
                CorrelationKey = $"manufacture:{runId:N}",
                RunId = runId,
                StartedBy = request.StartedBy,
                Variables = variables,
                Documents = documents,
            },
            ct).ConfigureAwait(false);
        if (started.IsSuccess)
        {
            return Result<Guid, ManufactureStartFailure>.Success(started.Value);
        }

        // The run starter's string channel carries only its fixed refusals, and real outages throw, so every failure here
        // is one the caller can read and fix: no active version, an unloadable definition, an unpinnable node.
        return await RemoveAfterFailureAsync(runId, ManufactureStartFailureKind.Unstartable, started.Error).ConfigureAwait(false);
    }

    private static Result<Guid, ManufactureStartFailure> Fail(ManufactureStartFailureKind kind, string message) =>
        Result<Guid, ManufactureStartFailure>.Failure(new ManufactureStartFailure(kind, message));

    /// <summary>
    ///     A provider error is the runtime being unreachable, such as the sandbox engine being down, so a retry may
    ///     succeed. A validation error is a request the provider refused. Anything else is the host's failure.
    /// </summary>
    private static ManufactureStartFailureKind KindOf(AgentError error) => error.Code switch
    {
        AgentErrorCode.ProviderError => ManufactureStartFailureKind.Unavailable,
        AgentErrorCode.Validation => ManufactureStartFailureKind.Invalid,
        _ => ManufactureStartFailureKind.Failed,
    };

    /// <summary>
    ///     Removes the workspace of a start that reported <paramref name="error"/> before any run row was written, so the
    ///     workspace belongs to no run, and returns that failure, naming a removal failure too when there is one.
    ///     <see cref="CancellationToken.None"/>: the undo must run even when the caller has given up, or the workspace
    ///     waits out the sweeper's grace period.
    /// </summary>
    private async Task<Result<Guid, ManufactureStartFailure>> RemoveAfterFailureAsync(
        Guid runId, ManufactureStartFailureKind kind, string error)
    {
        var removed = await _workspaces.RemoveAsync(runId, CancellationToken.None).ConfigureAwait(false);
        return removed.IsSuccess
            ? Fail(kind, error)
            : Fail(kind, $"{error} The run's workspace could not be removed and is left for the sweeper: {removed.Error.Message}");
    }

    /// <summary>
    ///     Removes the workspace of a start whose standing-instructions read threw, never letting the removal replace
    ///     <paramref name="readFault"/>. A removal that fails or throws is recorded on the read fault under
    ///     <see cref="WorkspaceRemovalFailureKey"/>, the exception counterpart of the start-failure path naming both
    ///     failures in its error text. The read fault keeps its own type, so a caller that handles an
    ///     <see cref="OperationCanceledException"/> or an <see cref="IOException"/> still sees one, where an
    ///     <see cref="AggregateException"/> would change what every caller has to catch.
    /// </summary>
    private async Task RemoveOrphanAsync(Guid runId, Exception readFault)
    {
        try
        {
            var removed = await _workspaces.RemoveAsync(runId, CancellationToken.None).ConfigureAwait(false);
            if (removed.IsFailure)
            {
                readFault.Data[WorkspaceRemovalFailureKey] = removed.Error.Message;
            }
        }
        catch (Exception removalFault)
        {
            readFault.Data[WorkspaceRemovalFailureKey] = removalFault.Message;
        }
    }

    private RepositoryConfig? FindRepository(string? name) =>
        _config.Repositories.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
}
