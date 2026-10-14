using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daedalus.Agents.Decisions;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Phase 2.10: decides, once per node visit, which of its role's allowed models an implement or review turn runs on
///     and which tool families it keeps, records the decision as a <see cref="WorkflowRunRecord.NodeSelectionKind"/> run
///     record, and sets the request's <see cref="SubagentRunRequest.Model"/> and <see cref="SubagentRunRequest.ToolFilter"/>.
/// </summary>
/// <remarks>
///     <para>
///     <b>Which turns.</b> Only a workflow turn whose pinned skill is <see cref="ReviewHandoff.ImplementSkillName"/> or
///     <see cref="ReviewHandoff.ReviewSkillName"/>. The role is the <see cref="AgentConfig"/> whose <c>Name</c> is the
///     pin's agent name; its menu is that entry's <c>AllowedModels</c> and <c>Tools</c> and its default model, which is
///     the pinned agent definition's model, the charter's for a chartered role, or the chat provider's default when the
///     definition names none. That is the model Thalos runs the agent on without an override. Every other turn, and a
///     turn whose role has no configuration entry, is forwarded unchanged. The review replay process runs the same
///     review node, so its turns are decided too.
///     </para>
///     <para>
///     <b>Once per visit, reused on a redelivery.</b> The record is keyed by the run and its <c>CurrentSeq</c>, which
///     every lens pass and every redelivery of one dispatch share. A record already at that seq is read back and its
///     decision used, so a replayed dispatch runs on what the first attempt ran on. The unique index
///     <see cref="WorkflowRunRecord.NodeSelectionIndexName"/> settles two deliveries deciding at once: the one whose
///     append loses reads the winner's record back and runs on that, never on its own decision.
///     </para>
///     <para>
///     <b>A fallback is today's request.</b> When the decision fell back (no client, a failure, a slow or unconfident
///     answer, an empty menu), the model is left unset rather than set to the default it records, so the turn is the
///     request it was before this existed and Thalos picks the agent's own model as always. A taken decision's model is
///     one of the role's allowed models, which Thalos itself checks against the agent's <c>AllowedModels</c>. The filter is
///     set whenever the decision narrowed the tool list, whatever the model; after any rejected review the decider is
///     told not to narrow, so implement and review get their full lists from then on.
///     </para>
///     <para>
///     <b>A decision that cannot be recorded fails the node</b>, the same rule as <see cref="ReviewLensRunner"/>: a turn
///     run on a decision a redelivery could not read back is a turn that could be replayed differently. A failed read or
///     write, or a record that does not parse, is logged and returned as a <see cref="AgentErrorCode.StoreError"/>, and no
///     turn runs. Cancellation of <c>ct</c> itself still propagates.
///     </para>
///     <para>
///     <b>Placement.</b> Outside <see cref="ReviewLensRunner"/>, so the three lens passes of a review share one decision
///     and so one cached prompt prefix; see <see cref="WorkflowNodeDispatcherFactory"/>.
///     </para>
/// </remarks>
/// <param name="inner">The runner the decided request is forwarded to.</param>
/// <param name="decider">Asks for the decision; never fails.</param>
/// <param name="scopes">Creates the scope the record store, the agent catalog and the chat provider are resolved from.</param>
/// <param name="clock">Timestamps each record.</param>
/// <param name="options">The configured agents, whose menus a decision chooses from.</param>
/// <param name="logger">Logs a role without configuration, and a record that could not be read or written.</param>
internal sealed partial class NodeSelectionRunner(
    ISubagentRunner inner,
    NodeSelectionDecider decider,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    DaedalusAgentsOptions options,
    ILogger<NodeSelectionRunner> logger) : ISubagentRunner
{
    private static readonly FrozenSet<string> EligibleSkills =
        new[] { ReviewHandoff.ImplementSkillName, ReviewHandoff.ReviewSkillName }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ISubagentRunner _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly NodeSelectionDecider _decider = decider ?? throw new ArgumentNullException(nameof(decider));
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly DaedalusAgentsOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<NodeSelectionRunner> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Caller is not WorkflowCaller caller || PinOf(caller.Run) is not { } pin)
            return await _inner.RunAsync(request, ct);

        var role = _options.Agents.FirstOrDefault(a => string.Equals(a.Name, pin.AgentName, StringComparison.Ordinal));
        if (role is null)
        {
            LogRoleNotConfigured(_logger, pin.AgentName, caller.Run.CurrentNode, caller.Run.Id);
            return await _inner.RunAsync(request, ct);
        }

        var selection = await SelectAsync(caller, pin, role, ct);
        if (selection.IsFailure)
            return Result<AgentTurnResult, AgentError>.Failure(selection.Error);

        return await _inner.RunAsync(Apply(request, selection.Value), ct);
    }

    /// <summary>The current node's pin, when it is one of the two decided skills.</summary>
    private static NodePin? PinOf(WorkflowRun run) =>
        run.Manifest is not null
        && run.Manifest.Nodes.TryGetValue(run.CurrentNode, out var pin)
        && EligibleSkills.Contains(pin.SkillName)
            ? pin
            : null;

    /// <summary>
    ///     The request a decision gives: its model only when the decision was taken, see the type's remarks, and its tool
    ///     filter whenever one was kept.
    /// </summary>
    private static SubagentRunRequest Apply(SubagentRunRequest request, NodeSelection selection) => request with
    {
        Model = selection.Fallback is null ? selection.Model : null,
        ToolFilter = selection.ToolFilter,
    };

    /// <summary>The visit's recorded decision, or a new one, recorded before it is returned.</summary>
    private async ValueTask<Result<NodeSelection, AgentError>> SelectAsync(
        WorkflowCaller caller, NodePin pin, AgentConfig role, CancellationToken ct)
    {
        var run = caller.Run;
        await using var scope = _scopes.CreateAsyncScope();
        var records = scope.ServiceProvider.GetRequiredService<IWorkflowRunRecordStore>();

        var recorded = await ReadAsync(records, run, ct);
        if (recorded.IsFailure || recorded.Value is not null)
            return recorded.IsFailure
                ? Result<NodeSelection, AgentError>.Failure(recorded.Error)
                : Result<NodeSelection, AgentError>.Success(recorded.Value!);

        var context = new NodeSelectionContext(
            pin.AgentName,
            run.CurrentNode,
            run.Variables.TryGetValue(ReviewHandoff.WorkIntentKey, out var intent) && intent is string text ? text : "",
            run.Visits.TryGetValue(run.CurrentNode, out var visit) ? visit : 1,
            MenuOf(role, pin, scope.ServiceProvider),
            NarrowTools: !AfterRejection(run));
        var decided = await _decider.DecideAsync(context, ct);

        var written = await AppendAsync(records, caller, run, decided, ct);
        if (written.IsFailure)
            return Result<NodeSelection, AgentError>.Failure(written.Error);
        if (written.Value)
            return Result<NodeSelection, AgentError>.Success(decided);

        // Another delivery of this visit recorded first. Its decision is the visit's, so it is the one this turn runs on.
        LogConcurrentDuplicate(_logger, run.Id, run.CurrentSeq);
        var winner = await ReadAsync(records, run, ct);
        if (winner.IsFailure)
            return Result<NodeSelection, AgentError>.Failure(winner.Error);

        return winner.Value is { } won
            ? Result<NodeSelection, AgentError>.Success(won)
            : Result<NodeSelection, AgentError>.Failure(AgentError.StoreError(NotRecorded, "the record that won the unique index could not be read back"));
    }

    /// <summary>Whether a review was rejected earlier in the run: either node is on a later visit.</summary>
    private static bool AfterRejection(WorkflowRun run) =>
        (run.Visits.TryGetValue("review", out var reviews) && reviews > 1)
        || (run.Visits.TryGetValue("implement", out var implements) && implements > 1);

    /// <summary>The role's menu: its default model, its allowed models and its tool patterns, in configured order.</summary>
    private static RoleMenu MenuOf(AgentConfig role, NodePin pin, IServiceProvider services)
    {
        var catalog = services.GetRequiredService<IAgentCatalog>();
        var model = catalog.TryGet(pin.AgentId, pin.AgentRevision, out var definition) ? definition?.Model : null;
        var defaultModel = string.IsNullOrWhiteSpace(model) ? services.GetRequiredService<IChatClientProvider>().DefaultModel : model;

        // An empty Tools list means every tool, the same default the agent's own definition gets.
        IReadOnlyList<string> tools = role.Tools.Count == 0 ? ["*"] : [.. role.Tools];
        return new RoleMenu(defaultModel, [.. role.AllowedModels], tools);
    }

    /// <summary>The decision recorded for the run's current seq, or <see langword="null"/> when there is none yet.</summary>
    private async ValueTask<Result<NodeSelection?, AgentError>> ReadAsync(IWorkflowRunRecordStore records, WorkflowRun run, CancellationToken ct)
    {
        IReadOnlyList<WorkflowRunRecord> found;
        try
        {
            found = await records.ListAsync(run.Id, WorkflowRunRecord.NodeSelectionKind, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogReadFailed(_logger, ex, run.Id, run.CurrentSeq);
            return Result<NodeSelection?, AgentError>.Failure(AgentError.StoreError(NotRead, ex.Message));
        }

        var record = found.FirstOrDefault(r => r.Seq == run.CurrentSeq);
        if (record is null)
            return Result<NodeSelection?, AgentError>.Success(null);

        var parsed = Parse(record.PayloadJson);
        if (parsed is null)
        {
            LogRecordUnreadable(_logger, run.Id, run.CurrentSeq);
            return Result<NodeSelection?, AgentError>.Failure(AgentError.StoreError(NotRead, "the recorded decision does not parse"));
        }

        LogReused(_logger, run.Id, run.CurrentSeq);
        return Result<NodeSelection?, AgentError>.Success(parsed);
    }

    /// <summary>
    ///     Appends the decision at the run's current seq and node, as <paramref name="caller"/>. Returns true when it was
    ///     written, false when the unique index refused it because another delivery recorded this visit first.
    /// </summary>
    private async ValueTask<Result<bool, AgentError>> AppendAsync(
        IWorkflowRunRecordStore records, WorkflowCaller caller, WorkflowRun run, NodeSelection selection, CancellationToken ct)
    {
        var record = WorkflowRunRecord.Create(
            run.Id, run.CurrentSeq, run.CurrentNode, WorkflowRunRecord.NodeSelectionKind, caller.Id, run.StartedBy?.Id,
            JsonSerializer.Serialize(SelectionPayload.From(selection)), _clock.GetUtcNow().UtcDateTime);
        if (record.IsFailure)
        {
            LogRecordRejected(_logger, run.Id, run.CurrentSeq, record.Error);
            return Result<bool, AgentError>.Failure(AgentError.StoreError(NotRecorded, record.Error));
        }

        try
        {
            await records.AppendAsync(record.Value, ct).ConfigureAwait(false);
            return Result<bool, AgentError>.Success(true);
        }
        catch (DbUpdateException ex) when (IsAlreadyRecorded(ex))
        {
            return Result<bool, AgentError>.Success(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Any failure other than this call's own cancellation, including a timeout surfacing as a cancellation nobody
            // asked for, is a decision that was not recorded, so the turn does not run on it.
            LogWriteFailed(_logger, ex, run.Id, run.CurrentSeq);
            return Result<bool, AgentError>.Failure(AgentError.StoreError(NotRecorded, ex.Message));
        }
    }

    /// <summary>Whether the failure is the unique index on (run, seq) refusing a second node-selection record.</summary>
    private static bool IsAlreadyRecorded(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && string.Equals(pg.ConstraintName, WorkflowRunRecord.NodeSelectionIndexName, StringComparison.Ordinal);

    /// <summary>The recorded decision, or <see langword="null"/> when the payload is not one this type wrote.</summary>
    private static NodeSelection? Parse(string payloadJson)
    {
        SelectionPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SelectionPayload>(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Model) || string.IsNullOrWhiteSpace(payload.Level))
            return null;

        // Thalos refuses an empty filter or a blank entry, so a record holding one would fail every replay of the visit.
        if (payload.ToolFilter is { } filter && (filter.Count == 0 || filter.Any(string.IsNullOrWhiteSpace)))
            return null;

        return new NodeSelection(payload.Model, payload.ToolFilter, payload.Level, payload.KeptFamilies, payload.Fallback, payload.Confidence);
    }

    private const string NotRecorded = "This node's model and tool decision could not be recorded, so the node does not run on it.";

    private const string NotRead = "This node's recorded model and tool decision could not be read, so the node does not run.";

    /// <summary>
    ///     The record's payload, <c>{ level, model, tool_filter, kept_families, fallback, confidence }</c>. Tool names
    ///     appear here, unlike in telemetry: a run record is the run's own audit, read only by people who can read the run.
    /// </summary>
    private sealed record SelectionPayload(
        [property: JsonPropertyName("level")] string Level,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("tool_filter")] IReadOnlyList<string>? ToolFilter,
        [property: JsonPropertyName("kept_families")] int KeptFamilies,
        [property: JsonPropertyName("fallback")] string? Fallback,
        [property: JsonPropertyName("confidence")] double? Confidence)
    {
        public static SelectionPayload From(NodeSelection selection) =>
            new(selection.Level, selection.Model, selection.ToolFilter, selection.KeptFamilies, selection.Fallback, selection.Confidence);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {Agent} on node {Node} of run {RunId} has no Thalos:Agents entry; the node runs undecided")]
    private static partial void LogRoleNotConfigured(ILogger logger, string agent, string node, Guid runId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Run {RunId} at seq {Seq} reuses its recorded node decision")]
    private static partial void LogReused(ILogger logger, Guid runId, long seq);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Run {RunId} at seq {Seq} was decided by another delivery first; that decision applies")]
    private static partial void LogConcurrentDuplicate(ILogger logger, Guid runId, long seq);

    [LoggerMessage(Level = LogLevel.Error, Message = "The node decision of run {RunId} at seq {Seq} could not be read; the node fails")]
    private static partial void LogReadFailed(ILogger logger, Exception exception, Guid runId, long seq);

    [LoggerMessage(Level = LogLevel.Error, Message = "The recorded node decision of run {RunId} at seq {Seq} does not parse; the node fails")]
    private static partial void LogRecordUnreadable(ILogger logger, Guid runId, long seq);

    [LoggerMessage(Level = LogLevel.Error, Message = "The node decision of run {RunId} at seq {Seq} was invalid ({Reason}); the node fails")]
    private static partial void LogRecordRejected(ILogger logger, Guid runId, long seq, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The node decision of run {RunId} at seq {Seq} could not be written; the node fails")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, Guid runId, long seq);
}
