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
using Thalos.Runtime;
using Thalos.Tools;
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
/// <param name="agents">Serves the pinned agent definition: its model and the tools Thalos checks a turn against.</param>
/// <param name="chatProvider">Names the model an agent whose definition names none runs on.</param>
/// <param name="toolCatalog">Resolves the tools an agent actually has on this host.</param>
/// <param name="agentFactory">Checks a decision's overrides exactly as the engine will before the turn runs.</param>
/// <param name="logger">Logs a role without configuration, and a record that could not be read or written.</param>
internal sealed partial class NodeSelectionRunner(
    ISubagentRunner inner,
    NodeSelectionDecider decider,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    DaedalusAgentsOptions options,
    IAgentCatalog agents,
    IChatClientProvider chatProvider,
    IToolCatalog toolCatalog,
    IAgentFactory agentFactory,
    ILogger<NodeSelectionRunner> logger) : ISubagentRunner
{
    /// <summary>
    ///     Fallback reason when Thalos refused the decided tool filter, or the model with it: the visit runs on the full
    ///     tool list, and on the decided model only when Thalos accepts that alone.
    /// </summary>
    public const string ToolsUnresolved = "tools-unresolved";

    /// <summary>
    ///     Fallback reason when Thalos could not check the decided overrides: a failure other than a validation refusal,
    ///     an exception, or no agent definition to check against. The visit runs on the full tool list and its own model.
    /// </summary>
    public const string ValidateFailed = "error:Validate";

    private static readonly FrozenSet<string> EligibleSkills =
        new[] { ReviewHandoff.ImplementSkillName, ReviewHandoff.ReviewSkillName }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ISubagentRunner _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly NodeSelectionDecider _decider = decider ?? throw new ArgumentNullException(nameof(decider));
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly DaedalusAgentsOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IAgentCatalog _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    private readonly IChatClientProvider _chatProvider = chatProvider ?? throw new ArgumentNullException(nameof(chatProvider));
    private readonly IToolCatalog _tools = toolCatalog ?? throw new ArgumentNullException(nameof(toolCatalog));
    private readonly IAgentFactory _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
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
        Model = ChosenModel(selection),
        ToolFilter = selection.ToolFilter,
    };

    /// <summary>
    ///     The model a selection sets on the turn: its own when the decision was taken, or when only its tools fell back
    ///     and Thalos accepted the decided model alone; otherwise none, so the agent runs on its own model as before.
    /// </summary>
    private static string? ChosenModel(NodeSelection selection) =>
        selection.Fallback is null
        || (string.Equals(selection.Fallback, ToolsUnresolved, StringComparison.Ordinal)
            && !string.Equals(selection.Level, NodeSelectionMapper.DefaultLevel, StringComparison.Ordinal))
            ? selection.Model
            : null;

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

        // A definition the catalog cannot serve is not this runner's failure to report: the turn goes on with the provider's
        // default model as its recorded default, and Thalos fails it with its own AgentNotFound if it cannot serve the agent
        // either. With decisions off nothing here can fail a node that would otherwise run.
        if (!_agents.TryGet(pin.AgentId, pin.AgentRevision, out var definition) || definition is null)
        {
            LogDefinitionMissing(_logger, pin.AgentName, run.CurrentNode, run.Id);
            definition = null;
        }

        var menu = await MenuOfAsync(role, definition, ct);
        var context = new NodeSelectionContext(
            pin.AgentName,
            run.CurrentNode,
            run.Variables.TryGetValue(ReviewHandoff.WorkIntentKey, out var intent) && intent is string text ? text : "",
            run.Visits.TryGetValue(run.CurrentNode, out var visit) ? visit : 1,
            menu,
            NarrowTools: !AfterRejection(run));
        var decided = await _decider.DecideAsync(
            context, (selection, token) => SettleAsync(definition, menu, selection, run, token), ct);

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

    /// <summary>
    ///     The role's menu: its default model, its allowed models, and those of its configured tool patterns, in configured
    ///     order, that match at least one tool the agent resolves on this host. A pattern for a source this host lacks, such
    ///     as <c>sandbox__build</c> in local mode or <c>issues__*</c> without a token, is not offered, so the decision can
    ///     never keep it and Thalos never refuses it. When the tools cannot be resolved the configured patterns are the
    ///     menu, and <see cref="SettleAsync"/> still guards the turn.
    /// </summary>
    private async ValueTask<RoleMenu> MenuOfAsync(AgentConfig role, AgentDefinition? definition, CancellationToken ct)
    {
        var defaultModel = string.IsNullOrWhiteSpace(definition?.Model) ? _chatProvider.DefaultModel : definition.Model;

        // An empty Tools list means every tool, the same default the agent's own definition gets.
        IReadOnlyList<string> configured = role.Tools.Count == 0 ? ["*"] : [.. role.Tools];

        // Resolved only when a decision can narrow: with decisions off the filter is always null, so the resolution would
        // be a tool-source round trip on every visit for nothing. Thalos' own cached resolution is not public, so this is
        // a direct IToolCatalog call.
        var resolved = definition is not null && _decider.HasClient ? await ResolvedToolNamesAsync(definition, ct) : null;
        IReadOnlyList<string> offered = resolved is null
            ? configured
            : [.. configured.Where(pattern => resolved.Any(name => Glob.IsMatch(pattern, name)))];
        return new RoleMenu(defaultModel, [.. role.AllowedModels], offered);
    }

    /// <summary>The qualified names of the tools the agent resolves, or <see langword="null"/> when resolution failed.</summary>
    private async ValueTask<IReadOnlyList<string>?> ResolvedToolNamesAsync(AgentDefinition definition, CancellationToken ct)
    {
        try
        {
            var resolved = await _tools.ResolveAsync(definition, ct).ConfigureAwait(false);
            if (resolved.IsSuccess)
                return [.. resolved.Value.Select(tool => tool.Name)];

            // The code only: the message can name a tool source or a tool.
            LogToolsUnresolvable(_logger, definition.Name, resolved.Error.Code.ToString());
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogToolsUnresolvable(_logger, definition.Name, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    ///     The safety net: Thalos checks the decided overrides exactly as it will on the turn.
    ///     <list type="bullet">
    ///         <item>Accepted: the decision stands.</item>
    ///         <item>Refused, a validation error: the full tool list, the decided model only if Thalos accepts that alone,
    ///             and the fallback <see cref="ToolsUnresolved"/>.</item>
    ///         <item>Not checked, any other failure: the full tool list, the agent's own model, and the fallback
    ///             <see cref="ValidateFailed"/>.</item>
    ///     </list>
    ///     An earlier fallback, such as <c>low-confidence</c>, is kept rather than replaced: it says why the model is the
    ///     default, which explains more. Called by the decider before it tags and counts, so the fallback is told like any
    ///     other.
    /// </summary>
    private async ValueTask<NodeSelection> SettleAsync(
        AgentDefinition? definition, RoleMenu menu, NodeSelection selection, WorkflowRun run, CancellationToken ct)
    {
        var model = ChosenModel(selection);
        if (model is null && selection.ToolFilter is null)
            return selection;

        var check = await CheckAsync(definition, model, selection.ToolFilter, ct);
        if (check == Check.Accepted)
            return selection;

        var modelKept = check == Check.Refused && model is not null
            && await CheckAsync(definition, model, null, ct) == Check.Accepted;
        var reason = check == Check.Refused ? ToolsUnresolved : ValidateFailed;
        LogOverridesNotApplied(_logger, run.Id, run.CurrentNode, reason, modelKept);
        var fullList = selection with
        {
            ToolFilter = null,
            KeptFamilies = NodeSelectionMapper.CountFamilies(menu.Tools),
            Fallback = selection.Fallback ?? reason,
        };
        return modelKept
            ? fullList
            : fullList with { Model = menu.DefaultModel, Level = NodeSelectionMapper.DefaultLevel };
    }

    /// <summary>What Thalos said about a turn's overrides.</summary>
    private enum Check
    {
        /// <summary>Thalos accepts them.</summary>
        Accepted,

        /// <summary>Thalos refused them as outside the agent's configuration: a validation error.</summary>
        Refused,

        /// <summary>Thalos could not check them: another error, an exception, or no definition to check against.</summary>
        Failed,
    }

    /// <summary>Asks Thalos whether it accepts these overrides for the agent, telling a refusal apart from a failed check.</summary>
    private async ValueTask<Check> CheckAsync(AgentDefinition? definition, string? model, IReadOnlyList<string>? toolFilter, CancellationToken ct)
    {
        if (definition is null)
            return Check.Failed;

        try
        {
            var checkedOverrides = await _agentFactory.ValidateOverridesAsync(definition, model!, toolFilter!, ct).ConfigureAwait(false);
            if (checkedOverrides.IsSuccess)
                return Check.Accepted;

            if (checkedOverrides.Error.Code == AgentErrorCode.Validation)
                return Check.Refused;

            LogCheckFailed(_logger, definition.Name, checkedOverrides.Error.Code.ToString());
            return Check.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogCheckFailed(_logger, definition.Name, ex.GetType().Name);
            return Check.Failed;
        }
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

        var parsed = NodeSelectionRecord.Parse(record.PayloadJson);
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
            NodeSelectionRecord.Serialize(selection), _clock.GetUtcNow().UtcDateTime);
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

    private const string NotRecorded = "This node's model and tool decision could not be recorded, so the node does not run on it.";

    private const string NotRead = "This node's recorded model and tool decision could not be read, so the node does not run.";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {Agent} on node {Node} of run {RunId} has no Thalos:Agents entry; the node runs undecided")]
    private static partial void LogRoleNotConfigured(ILogger logger, string agent, string node, Guid runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {Agent} on node {Node} of run {RunId} has no definition this host can serve; the provider's default model is recorded as its default")]
    private static partial void LogDefinitionMissing(ILogger logger, string agent, string node, Guid runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The tools of agent {Agent} could not be resolved ({ErrorCode}); its configured patterns are offered")]
    private static partial void LogToolsUnresolvable(ILogger logger, string agent, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Thalos could not check the decided overrides of agent {Agent} ({Failure}); the full tool list applies")]
    private static partial void LogCheckFailed(ILogger logger, string agent, string failure);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The decided overrides for node {Node} of run {RunId} were not applied ({Reason}); the full tool list applies, decided model kept: {ModelKept}")]
    private static partial void LogOverridesNotApplied(ILogger logger, Guid runId, string node, string reason, bool modelKept);

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
