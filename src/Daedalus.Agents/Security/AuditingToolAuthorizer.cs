using System.Text.Json;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Thalos;
using Thalos.Tools;
using ZeroAlloc.Authorization;

namespace Daedalus.Agents.Security;

/// <summary>
///     Wraps the host's <see cref="IToolAuthorizer"/> so every workspace write a workflow run is allowed leaves a
///     <see cref="WorkflowRunRecord.WorkspaceWriteKind"/> record, with the run, its sequence number, the node, the
///     caller, the run's starter, the tool and the path. A write that cannot be recorded is denied with
///     <see cref="AuditUnavailable"/>.
/// </summary>
/// <remarks>
///     <para>
///     <b>What is audited.</b> A call is recorded only when all three hold: the inner authorizer allowed it, the caller
///     is a <see cref="WorkflowCaller"/>, and the tool matches one of <paramref name="auditedPatterns"/>. The patterns
///     are every <c>Thalos:ToolPolicies</c> pattern bound to <see cref="WorkspaceWritePolicy.PolicyName"/> or
///     <see cref="CSharpWritePolicy.PolicyName"/>, so the audit set is the grant's own, reviewed configuration: a tool
///     rebound to either, as <c>roslyn__apply_*</c> is to <c>csharp-write</c>, is audited from that change on without
///     touching this type. A denied call is
///     not recorded here; <c>AuthorizingAIFunction</c> already publishes a denial notification for it. A chat or
///     scheduled caller has no run to record against and cannot reach a run's worktree (its inbound <c>thalos.*</c>
///     claims are dropped by <see cref="ClaimsSecurityContext"/>), so it is not recorded either.
///     </para>
///     <para>
///     <b>When.</b> The record is appended after the decision and before the tool runs, so it records a write the
///     host allowed, not one that succeeded: the tool itself can still refuse the path, for example a protected file
///     or an extension outside the grant.
///     </para>
///     <para>
///     <b>What.</b> The payload is <c>{ tool, path }</c> only. The path is read from <c>path</c>, the
///     <c>workspace__*</c> argument, or <c>filePath</c>, Roslyn's. Content and edit text are never stored: they can be
///     large, and the run's own commit already holds what was written. A path longer than <see cref="MaxPathLength"/> is
///     not recorded, and the call is denied.
///     </para>
///     <para>
///     <b>Fail closed (ruling R25).</b> An invalid record or a failed append is logged and the call is denied with
///     <see cref="AuditUnavailable"/>; the exception never escapes. Cancellation of <c>ct</c> itself still propagates.
///     </para>
///     <para>
///     The store is resolved from a fresh scope per call, so this singleton never captures a scoped dependency.
///     </para>
/// </remarks>
/// <param name="inner">The authorizer whose decision is audited.</param>
/// <param name="auditedPatterns">The tool globs whose allowed calls are recorded.</param>
/// <param name="scopes">Creates the scope each append resolves <see cref="IWorkflowRunRecordStore"/> from.</param>
/// <param name="clock">Timestamps each record.</param>
/// <param name="logger">Logs a record that could not be written.</param>
internal sealed partial class AuditingToolAuthorizer(
    IToolAuthorizer inner,
    IReadOnlyList<string> auditedPatterns,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<AuditingToolAuthorizer> logger) : IToolAuthorizer
{
    /// <summary>The denial reason for an allowed write whose audit record could not be written.</summary>
    public const string AuditUnavailable = "audit unavailable";

    /// <summary>
    ///     The longest path, in UTF-16 characters, that is recorded. A longer one is denied, not truncated: truncation
    ///     would collapse different paths into one ambiguous record, and the file system refuses such a path anyway.
    /// </summary>
    public const int MaxPathLength = 4096;

    /// <inheritdoc />
    public async ValueTask<ToolAuthorizationDecision> AuthorizeAsync(
        ISecurityContext caller, string qualifiedToolName, JsonElement arguments, CancellationToken ct)
    {
        var decision = await inner.AuthorizeAsync(caller, qualifiedToolName, arguments, ct).ConfigureAwait(false);
        if (!decision.Allowed || caller is not WorkflowCaller workflow || !IsAudited(qualifiedToolName))
        {
            return decision;
        }

        var run = workflow.Run;
        var path = TryString(arguments, "path") ?? TryString(arguments, "filePath");
        if (path?.Length > MaxPathLength)
        {
            LogAuditRejected(logger, qualifiedToolName, run.Id, $"path is {path.Length} characters, more than {MaxPathLength}");
            return ToolAuthorizationDecision.Deny(AuditUnavailable);
        }

        var record = WorkflowRunRecord.Create(
            run.Id, run.CurrentSeq, run.CurrentNode, WorkflowRunRecord.WorkspaceWriteKind, caller.Id, run.StartedBy?.Id,
            JsonSerializer.Serialize(new { tool = qualifiedToolName, path }), clock.GetUtcNow().UtcDateTime);
        if (record.IsFailure)
        {
            LogAuditRejected(logger, qualifiedToolName, run.Id, record.Error);
            return ToolAuthorizationDecision.Deny(AuditUnavailable);
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWorkflowRunRecordStore>()
                .AppendAsync(record.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Any failure other than this call's own cancellation, including a timeout surfacing as a cancellation
            // the caller never asked for, is a write that cannot be audited, so it is not allowed.
            LogAuditWriteFailed(logger, ex, qualifiedToolName, run.Id);
            return ToolAuthorizationDecision.Deny(AuditUnavailable);
        }

        return decision;
    }

    private bool IsAudited(string tool) => auditedPatterns.Any(pattern => Glob.IsMatch(pattern, tool));

    private static string? TryString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit record for {Tool} in run {RunId} could not be written; the call is denied")]
    private static partial void LogAuditWriteFailed(ILogger logger, Exception exception, string tool, Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit record for {Tool} in run {RunId} was invalid ({Reason}); the call is denied")]
    private static partial void LogAuditRejected(ILogger logger, string tool, Guid runId, string reason);
}
