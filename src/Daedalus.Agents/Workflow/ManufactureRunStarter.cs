using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     The real <see cref="IManufactureRunStarter"/>, registered by
///     <c>DaedalusAgentsServiceCollectionExtensions.AddDaedalusWorkflow</c> in place of
///     <see cref="DisabledManufactureRunStarter"/> whenever <c>Thalos:Workflow:Enabled</c> is
///     <see langword="true"/>. Shapes one call onto <see cref="WorkflowRunStarter.StartAsync"/>: the process is
///     always <c>manufacture</c>, the opening variable is always <c>work_intent</c>, and the current standing
///     instructions file — read fresh on every call, not cached, so an edit to it takes effect on the very next
///     run — is pinned into the manifest as <see cref="StandingInstructionsDocument"/>.
/// </summary>
/// <remarks>
///     <b>The correlation key is a fresh attempt id, not an idempotency key over the caller's input.</b>
///     <c>$"manufacture:{Guid.NewGuid():N}"</c> means two calls with the same <c>workIntent</c> text always start
///     two different runs — this type has no notion of "the same request retried" to collapse them under. The
///     key space <see cref="IWorkflowStore.StartAsync(WorkflowStartRequest,System.Threading.CancellationToken)"/>
///     enforces uniqueness over is global and permanent for the life of the database, so the prefix exists only
///     to make a run's correlation key human-readable in a query, not to partition it from any other process's
///     keys — no other process uses this one.
/// </remarks>
public sealed class ManufactureRunStarter(WorkflowRunStarter starter, string standingInstructionsPath) : IManufactureRunStarter
{
    /// <summary>The manifest document key the current standing instructions file is pinned under.</summary>
    public const string StandingInstructionsDocument = "standing_instructions";

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
    private readonly string _standingInstructionsPath = standingInstructionsPath ?? throw new ArgumentNullException(nameof(standingInstructionsPath));

    /// <inheritdoc />
    public async ValueTask<Result<Guid>> StartAsync(string workIntent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workIntent))
        {
            return Result<Guid>.Failure("workIntent must not be blank.");
        }

        if (workIntent.Length > MaxWorkIntentLength)
        {
            return Result<Guid>.Failure(
                $"workIntent must be at most {MaxWorkIntentLength} characters, but was {workIntent.Length}.");
        }

        var standingInstructions = File.Exists(_standingInstructionsPath)
            ? await File.ReadAllTextAsync(_standingInstructionsPath, ct).ConfigureAwait(false)
            : "";

        var correlationKey = $"manufacture:{Guid.NewGuid():N}";
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal) { ["work_intent"] = workIntent };
        var documents = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StandingInstructionsDocument] = standingInstructions,
        };

        return await _starter.StartAsync(ProcessName, correlationKey, variables, documents, ct).ConfigureAwait(false);
    }
}
