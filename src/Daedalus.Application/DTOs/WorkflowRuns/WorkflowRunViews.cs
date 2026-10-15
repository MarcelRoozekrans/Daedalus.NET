namespace Daedalus.Application.DTOs.WorkflowRuns;

/// <summary>Request body for <c>WorkflowRunsController.Resume</c>.</summary>
/// <param name="Signal">The signal the caller believes the run is parked awaiting.</param>
/// <param name="Payload">Carried into <c>WorkflowRun.Variables</c>["payload"] by <c>IWorkflowStore.ResumeAsync</c>.</param>
/// <param name="ApplyStandingInstructions">
///     Task B5. Defaults to <see langword="false"/>, so every existing caller of this endpoint is unaffected.
///     Set <see langword="true"/> only when a human operator has decided to accept the run's proposed standing
///     instructions — this is the one flag that makes <c>WorkflowRunGateway</c> write to disk at all.
/// </param>
/// <param name="DropFindings">
///     Phase 2.7. The ids of deferred findings the human decided not to file. An id the run does not have is a 422 and
///     nothing is resumed.
/// </param>
public sealed record ResumeWorkflowRunRequest(
    string Signal, string? Payload, bool ApplyStandingInstructions = false, IReadOnlyList<string>? DropFindings = null);

/// <summary>Request body for <c>WorkflowRunsController.Cancel</c>.</summary>
/// <param name="Reason">A human-readable reason recorded against the run; defaults when omitted.</param>
public sealed record CancelWorkflowRunRequest(string? Reason);

/// <summary>Response body for <c>WorkflowRunsController.Get</c>.</summary>
/// <param name="Id">The run's identity.</param>
/// <param name="Process">The process name this run is executing.</param>
/// <param name="ProcessVersion">The version of the process this run is executing.</param>
/// <param name="Status">The run's lifecycle status (<c>WorkflowStatus</c>, as text).</param>
/// <param name="CurrentNode">The node the run is currently positioned at.</param>
/// <param name="AwaitingSignal">The signal a parked run is waiting on, or <see langword="null"/> otherwise.</param>
/// <param name="LastError">The most recent error recorded against this run, or <see langword="null"/> if it has not failed.</param>
/// <param name="Pins">
///     The run's write-once agent/skill pins, keyed by task node name, or <see langword="null"/> for a run started
///     before manifests existed or through the legacy positional <c>StartAsync</c> overload.
/// </param>
/// <param name="StandingInstructionsDiff">
///     A unified-style line diff of the run's pinned standing instructions against its <c>retrospect</c>
///     proposal (see <c>StandingInstructionsWriter.Diff</c>), or
///     <see langword="null"/> when the run carries no proposal — including every run started before phase 2.4
///     task B4 added the <c>retrospect</c> node at all.
/// </param>
/// <param name="NodeUsage">
///     Each completed agent node's token and cache usage, in seq order, read off the run's completion events and never
///     off its variables. A node that ran no agent turn, such as a gate or a host action, has no entry.
/// </param>
/// <param name="PrUrl">The run's <c>pr_url</c> variable, which the <c>open-pull-request</c> action writes, or <see langword="null"/> before it has.</param>
/// <param name="UnreadablePullRequestLink">
///     The run's <c>pr_url</c> as stored when it is not an absolute http or https URL, and so is not shown as
///     <paramref name="PrUrl"/>; otherwise <see langword="null"/>.
/// </param>
/// <param name="StartedBy">The id of the principal that started the run, or <see langword="null"/> when it carries none.</param>
/// <param name="StartedByName">The display name of the principal that started the run, or <see langword="null"/> when it carries none.</param>
/// <param name="WriteAudit">Every workspace write the run was allowed, from its write-audit records, in seq order.</param>
/// <param name="DeferredFindings">
///     The deferred findings of the run's approving review visit, each marked dropped when the latest
///     <c>findings-dropped</c> record names it. Empty when the findings cannot be read. When only the drop records cannot be read, none is marked dropped and the error says so.
/// </param>
/// <param name="DeferredFindingsError">Why a review-evidence, findings-dropped or finding-filed record could not be read, otherwise <see langword="null"/>.</param>
/// <param name="FiledFindings">
///     What <c>file-review-findings</c> did with each deferred finding, and the pull-request summary, from its
///     <c>finding-filed</c> records. Empty before the action has run, and when those records cannot be read, which the error says.
/// </param>
/// <param name="Steps">
///     The run's event log in seq order: every start, completion, branch, park, resume, failure, retry and
///     cancellation.
/// </param>
/// <param name="Checked">
///     Each lens of the latest review visit as <paramref name="ReviewVisits"/> lists it, with its <c>checked[]</c> and its
///     findings, approving or rejecting; empty before review has run,
///     and when the review records cannot be read, which <paramref name="DeferredFindingsError"/> says.
/// </param>
/// <param name="NodeSelections">
///     The model and tool decision taken for each node visit, from the run's <c>node-selection</c> records, in seq order.
///     Empty when no node was decided. A record that cannot be read is not listed; <paramref name="NodeSelectionsError"/> says so.
/// </param>
/// <param name="NodeSelectionsError">Which node-selection records could not be read, by seq, otherwise <see langword="null"/>.</param>
/// <param name="FailureReason">
///     Why the run failed, derived on read, for a <c>Failed</c> run; <see langword="null"/> for every other status. It names the
///     review limit and its last rejecting lens, a blocked implement, the host's error, or else the node the run ended at.
/// </param>
/// <param name="ReviewVisits">
///     Every review visit of the run, newest first, read off its <c>review-evidence</c> records, each rejected one with the
///     <c>review_response</c> implement reported on the visit that answered it. Empty before review has run. A record that
///     cannot be read is left out, and <paramref name="ReviewVisitsError"/> says so.
/// </param>
/// <param name="ReviewVisitsError">
///     Which <c>review-evidence</c> or <c>review-response</c> records could not be read, by seq, otherwise <see langword="null"/>.
/// </param>
public sealed record WorkflowRunView(
    Guid Id,
    string Process,
    int ProcessVersion,
    string Status,
    string CurrentNode,
    string? AwaitingSignal,
    string? LastError,
    IReadOnlyDictionary<string, NodePinView>? Pins,
    string? StandingInstructionsDiff,
    IReadOnlyList<NodeUsageView> NodeUsage,
    Uri? PrUrl,
    string? UnreadablePullRequestLink,
    string? StartedBy,
    IReadOnlyList<WriteAuditView> WriteAudit,
    IReadOnlyList<DeferredFindingView> DeferredFindings,
    string? DeferredFindingsError,
    IReadOnlyList<FiledFindingView> FiledFindings,
    IReadOnlyList<WorkflowRunStepView> Steps,
    IReadOnlyList<LensCheckedView> Checked,
    string? StartedByName,
    IReadOnlyList<NodeSelectionView> NodeSelections,
    string? NodeSelectionsError,
    string? FailureReason,
    IReadOnlyList<ReviewVisitView> ReviewVisits,
    string? ReviewVisitsError);

/// <summary>One review visit of a run, as its <c>review-evidence</c> records hold it.</summary>
/// <param name="Seq">The seq of the review visit.</param>
/// <param name="Verdict">
///     <c>Unreadable</c> when any of the visit's records cannot be read, since its full set of lenses is then unknown; otherwise
///     <c>Rejected</c> when any lens rejected, and <c>Approved</c> when none did.
/// </param>
/// <param name="RejectingLens">The first lens that rejected on a <c>Rejected</c> visit; otherwise <see langword="null"/>.</param>
/// <param name="Lenses">
///     Each readable lens the visit ran, in the order it ran, with what it checked and the findings it reported; empty when no
///     record of the visit can be read.
/// </param>
/// <param name="ReviewResponse">
///     On a <c>Rejected</c> or <c>Unreadable</c> visit, the <c>review_response</c> implement reported on the next implement
///     visit, the one the rejection sent the run back to; <see langword="null"/> when that visit recorded none, when it has not
///     run yet, and on an approval.
///     Shown to the operator only: the reviewer's own dispatch never carries it.
/// </param>
public sealed record ReviewVisitView(long Seq, string Verdict, string? RejectingLens, IReadOnlyList<LensCheckedView> Lenses, string? ReviewResponse);

/// <summary>One finding a review lens reported, as the run page shows it.</summary>
/// <param name="File">The repository-relative file the finding is in.</param>
/// <param name="Line">The one-based line it is at.</param>
/// <param name="Scenario">How the defect shows.</param>
public sealed record ReviewFindingView(string File, int Line, string Scenario);

/// <summary>
///     The model and tool decision of one node visit, as its <c>node-selection</c> record holds it. The tool filter is
///     not here: tool names stay out of API views of decisions, as they stay out of telemetry.
/// </summary>
/// <param name="Seq">The seq of the node visit the decision belongs to.</param>
/// <param name="Node">The node.</param>
/// <param name="Model">
///     The model the node runs on. On a review whose lens is pinned (<c>Thalos:Decisions:PinnedLensModels</c>) that lens's
///     pass ran on its pinned model instead, which this view does not show: the node's <c>node-usage</c> run record holds one
///     <c>byModel</c> part per model it ran on, the per-lens models as cost analytics prices them.
/// </param>
/// <param name="Level">The demand level chosen, or the default's level when the decision fell back.</param>
/// <param name="KeptFamilies">How many tool families the node keeps.</param>
/// <param name="Fallback">Why the default applies, or <see langword="null"/> when the decision was taken.</param>
/// <param name="Confidence">The confidence of the demand answer, or <see langword="null"/> when Minos did not answer.</param>
public sealed record NodeSelectionView(long Seq, string Node, string Model, string Level, int KeptFamilies, string? Fallback, double? Confidence);

/// <summary>One entry of a run's event log, as the console's timeline shows it.</summary>
/// <param name="Seq">The seq the event was recorded at.</param>
/// <param name="Kind">The event kind, as the engine names it.</param>
/// <param name="FromNode">The node the run left, or <see langword="null"/> for the start.</param>
/// <param name="ToNode">The node the run is at after the event.</param>
/// <param name="Status">The run's status after the event.</param>
/// <param name="Outcome">The outcome the completed node reported, or <see langword="null"/>.</param>
/// <param name="Error">The failure or cancellation reason, or <see langword="null"/>.</param>
/// <param name="InputTokens">Input tokens of the agent turn the event closes, or <see langword="null"/> when it ran none.</param>
/// <param name="OutputTokens">Output tokens of that turn, or <see langword="null"/>.</param>
/// <param name="CacheReadTokens">Input tokens of that turn read from a prompt cache, or <see langword="null"/>.</param>
/// <param name="At">When the event was recorded.</param>
/// <param name="Actor">Who retried the node, for a Retried event; otherwise <see langword="null"/>.</param>
public sealed record WorkflowRunStepView(
    long Seq, string Kind, string? FromNode, string ToNode, string Status, string? Outcome, string? Error,
    int? InputTokens, int? OutputTokens, int? CacheReadTokens, DateTimeOffset At, string? Actor);

/// <summary>What one review lens recorded on a review visit: what it checked and the findings it reported.</summary>
/// <param name="Lens">The lens name.</param>
/// <param name="Checked">What the lens reports it checked.</param>
/// <param name="Findings">The findings the lens reported; empty on an approving lens.</param>
public sealed record LensCheckedView(string Lens, IReadOnlyList<string> Checked, IReadOnlyList<ReviewFindingView> Findings);

/// <summary>The write-once agent and skill pin of one task node, as the run's manifest recorded it.</summary>
/// <param name="AgentName">The agent the node runs.</param>
/// <param name="AgentId">The agent's catalog id.</param>
/// <param name="AgentRevision">The agent's revision when the run started.</param>
/// <param name="SkillName">The skill the node runs.</param>
/// <param name="SkillHash">The skill's content hash when the run started.</param>
public sealed record NodePinView(string AgentName, string AgentId, string? AgentRevision, string SkillName, string SkillHash);

/// <summary>Where one deferred finding went: <c>created</c> or <c>commented</c> on <paramref name="Issue"/>, or the <c>summary</c> on the pull request.</summary>
public sealed record FiledFindingView(string FindingId, string Mode, int Issue, Uri Url);

/// <summary>One deferred finding of the approving review visit, as the gate shows it.</summary>
/// <param name="Id">The finding's id, its lens and its number within that lens.</param>
/// <param name="Lens">The review lens that deferred it.</param>
/// <param name="File">The file the finding is in.</param>
/// <param name="Line">The line the finding is at.</param>
/// <param name="Title">The finding's one-line title.</param>
/// <param name="Scenario">How the defect shows.</param>
/// <param name="Reason">Why the reviewer deferred it instead of fixing it in the run.</param>
/// <param name="ExistingIssue">An open issue the reviewer found already tracking it, or <see langword="null"/>.</param>
/// <param name="Dropped">Whether the latest drop record names it, so it will not be filed.</param>
/// <param name="ExistingIssueUrl">
///     The web link of <paramref name="ExistingIssue"/>: in the repository of the run's pull request once there is
///     one, which is where <c>file-review-findings</c> resolves it, and before that, as at the gate, in the
///     allow-listed repository the run's workspace was created for. <see langword="null"/> when the finding names no
///     issue or neither repository can be read as a github.com one.
/// </param>
public sealed record DeferredFindingView(
    string Id, string Lens, string File, int Line, string Title, string Scenario, string Reason, int? ExistingIssue, bool Dropped,
    Uri? ExistingIssueUrl);

/// <summary>The token usage one completed agent node reported, read off its completion event.</summary>
/// <param name="Seq">The seq of the node execution the completion event closes.</param>
/// <param name="Node">The node that completed.</param>
/// <param name="InputTokens">Input tokens, cache reads and writes included.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CacheReadTokens">Input tokens read from a prompt cache.</param>
/// <param name="CacheWriteTokens">Input tokens written to a prompt cache.</param>
/// <param name="ModelId">
///     The model that served the node, or empty when the turn reported none. For a node that ran on several models, such as
///     a review with a pinned lens, the first; the node's <c>node-usage</c> record splits its tokens per model.
/// </param>
public sealed record NodeUsageView(long Seq, string Node, int InputTokens, int OutputTokens, int CacheReadTokens, int CacheWriteTokens, string ModelId);

/// <summary>One workspace write the tool authorizer allowed and recorded, read off the run's write-audit records.</summary>
/// <param name="Seq">The run's seq when the write was allowed.</param>
/// <param name="Node">The node the run was on.</param>
/// <param name="Tool">The qualified name of the write tool.</param>
/// <param name="Path">The path the call named, or <see langword="null"/> when it named none.</param>
/// <param name="StartedBy">The id of the principal that started the run, or <see langword="null"/> when it carries none.</param>
public sealed record WriteAuditView(long Seq, string Node, string Tool, string? Path, string? StartedBy);

/// <summary>One page of <c>GET /api/workflow-runs</c>.</summary>
/// <param name="Runs">The runs, newest first.</param>
/// <param name="Next">The opaque cursor for the next page, or <see langword="null"/> on the last page.</param>
public sealed record WorkflowRunList(IReadOnlyList<WorkflowRunListItem> Runs, string? Next);

/// <summary>
///     One run in the list: no variables, no raw error, and the board task it belongs to when there is one.
///     <paramref name="FailureReason"/> is the derived reason of a <c>Failed</c> run and <see langword="null"/> otherwise;
///     <paramref name="PrUrl"/> is the run's pull request link, <see langword="null"/> when it has none or it is not an http link.
/// </summary>
public sealed record WorkflowRunListItem(
    Guid Id, string Process, int ProcessVersion, string Status, string CurrentNode, string? AwaitingSignal,
    string? StartedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? TaskId, string? TaskCode, string? TaskTitle,
    string? StartedByName, string? FailureReason, Uri? PrUrl);

/// <summary>
///     What the console's hub sends when a run changes: which run, and where it now stands. Nothing else, ever: no
///     outcome, error, variable or payload, so it can go to every connected operator.
/// </summary>
/// <param name="Id">The run that changed.</param>
/// <param name="Status">The run's status after the change (<c>WorkflowStatus</c>, as text).</param>
/// <param name="CurrentNode">The node the run is at after the change.</param>
/// <param name="AwaitingSignal">The signal the run waits on after the change, or <see langword="null"/>.</param>
public sealed record WorkflowRunChanged(Guid Id, string Status, string CurrentNode, string? AwaitingSignal);
