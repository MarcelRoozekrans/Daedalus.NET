using System.Text.Json;
using Asp.Versioning;
using Daedalus.Agents.Security;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Agents;
using Daedalus.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Thalos.Workflow;

namespace Daedalus.Api.Controllers;

/// <summary>
///     Starts, resumes, cancels, retries and reads back manufacture runs. Every action on this controller requires
///     the <c>WorkflowResume</c> authorization policy (<c>Program.cs</c>: <c>developer</c> or <c>admin</c> role),
///     the same criterion <c>Thalos:ToolPolicies</c> binds <c>git__*</c>, <c>repoaction__*</c> and <c>manufacture__*</c>
///     to. It is enforced here by ASP.NET Core's own role-based authorization rather than <c>DefaultToolAuthorizer</c>,
///     because these are REST endpoints, not Thalos tool calls, and <c>DefaultToolAuthorizer</c> never sees a
///     request that never names a tool. Starting a run over REST and starting one through <c>manufacture__start</c>
///     are deliberately gated the same way, by two different mechanisms that happen to require the same role.
///     Retry is narrower than the rest: it re-runs a push on a run another person approved, so <see cref="Retry"/> additionally requires the
///     narrower <c>Admin</c> policy.
/// </summary>
/// <remarks>
///     <b>Resume, cancel and retry are deliberately not Thalos tools.</b> Resuming a gate is reachable only through this
///     controller. No agent — including one running as the run's own <c>WorkflowCaller</c> — can call it, because
///     it is never registered as a local tool source and therefore never appears in any agent's resolved tool
///     list. An agent that could resume its own approval gate would make every gate in the engine decorative. The
///     <c>WorkflowResume</c> policy above is a separate layer, for a separate caller shape: a human, or a
///     scheduled run's HTTP caller, presenting roles over a real <c>ClaimsPrincipal</c>. <c>WorkflowCaller</c> is
///     never one of those — it is a Thalos <c>ISecurityContext</c> an agent turn carries internally, never
///     something that reaches this controller's authorization pipeline at all — so this policy neither denies
///     nor could deny it; the tool-source absence above is what actually stops it.
///     <para>
///     <b>Starting is different: it is also a Thalos tool.</b> <c>manufacture__start</c>
///     (<see cref="Daedalus.Agents.Tools.DaedalusManufactureTools"/>) calls the very same
///     <see cref="Daedalus.Agents.Workflow.IManufactureRunStarter"/> this controller's <see cref="Start"/> action
///     does — starting a run is not the same hazard resuming one is, so it is deliberately reachable both ways,
///     with the tool path gated at the authorizer by <c>Thalos:ToolPolicies</c> instead of by absence.
///     </para>
/// </remarks>
[ApiController]
[WorkflowEngineEnabled]
[ApiVersion("1.0")]
[Route("api/workflow-runs")]
[Authorize(Policy = "WorkflowResume")]
[Produces("application/json")]
public sealed class WorkflowRunsController(WorkflowRunGateway runs) : ControllerBase
{
    /// <summary>The seconds a client is told to wait before retrying a start the host could not serve.</summary>
    private const string RetryAfterSeconds = "30";

    /// <summary>
    ///     Starts a new manufacture run for <paramref name="request"/>'s <see cref="StartWorkflowRunRequest.WorkIntent"/>
    ///     on the allow-listed repository its <see cref="StartWorkflowRunRequest.Repository"/> names. A blank intent or
    ///     a blank repository fails with 400 before <see cref="IManufactureRunStarter.StartAsync"/> is even called.
    ///     Past that, a failure the starter reports is mapped by its
    ///     <see cref="ManufactureStartFailureKind"/>: <c>Invalid</c>, such as a repository that is not allow-listed, is
    ///     400; <c>Unstartable</c>, such as a deactivated skill on a task node, is 422 naming the node; <c>Unavailable</c>,
    ///     a transient outage such as the sandbox runtime being down, is 503 with <c>Retry-After: 30</c>; <c>Disabled</c>,
    ///     the workflow engine being off, is 503 without Retry-After because no retry gets past a host setting; anything
    ///     else is 500.
    /// </summary>
    /// <param name="request">The request body.</param>
    /// <param name="starter">
    ///     Injected onto the action rather than the primary constructor — see this controller's own summary for
    ///     why <see cref="IManufactureRunStarter"/> is not one of its constructor parameters:
    ///     <c>ResumeToolBoundaryTests</c> and <c>ResumeSignalMismatchTests</c> construct this controller directly
    ///     with only a <see cref="WorkflowRunGateway"/>, and those tests are pinned to stay green unchanged.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    [ProducesResponseType(typeof(StartWorkflowRunResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Start(
        [FromBody] StartWorkflowRunRequest request, [FromServices] IManufactureRunStarter starter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.WorkIntent))
        {
            return Problem(detail: "WorkIntent must not be blank.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.Repository))
        {
            return Problem(detail: "Repository must not be blank.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        var startRequest = new ManufactureStartRequest(
            request.WorkIntent, request.Repository, RunPrincipals.From(caller, User.FindFirst("preferred_username")?.Value));
        var result = await starter.StartAsync(startRequest, ct);
        if (result.IsFailure)
        {
            var failure = result.Error;
            switch (failure.Kind)
            {
                case ManufactureStartFailureKind.Invalid:
                    return Problem(detail: failure.Message, statusCode: StatusCodes.Status400BadRequest);
                case ManufactureStartFailureKind.Unstartable:
                    return Problem(detail: failure.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                case ManufactureStartFailureKind.Unavailable:
                    Response.Headers.RetryAfter = RetryAfterSeconds;
                    return Problem(detail: failure.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
                case ManufactureStartFailureKind.Disabled:
                    return Problem(detail: failure.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
                default:
                    return Problem(detail: failure.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        }

        return CreatedAtAction(nameof(Get), new { id = result.Value }, new StartWorkflowRunResponse(result.Value));
    }

    /// <summary>Reads back one run's current state, including its write-once manifest pins when it has one.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(WorkflowRunView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var run = await runs.FindAsync(id, ct);
        return run is null ? NotFound() : Ok(await ToViewAsync(run, ct));
    }

    /// <summary>
    ///     Builds the view. Node usage comes from the run's completion events, never from its variables, which no
    ///     node's usage is written to. The write audit comes from the host-written records, which no agent can add
    ///     to or change.
    /// </summary>
    private async Task<WorkflowRunView> ToViewAsync(WorkflowRun run, CancellationToken ct)
    {
        var events = await runs.ListEventsAsync(run.Id, ct);
        var writes = await runs.ListRecordsAsync(run.Id, WorkflowRunRecord.WorkspaceWriteKind, ct);

        var (prUrl, unreadableLink) = ReadPrUrl(run);
        return new WorkflowRunView(
            run.Id,
            run.Process,
            run.ProcessVersion,
            run.Status.ToString(),
            run.CurrentNode,
            run.AwaitingSignal,
            run.LastError,
            run.Manifest?.Nodes,
            StandingInstructionsDiff: StandingInstructionsWriter.Diff(run),
            NodeUsage: [.. events.Where(e => e.Usage is not null && e.FromNode is not null).Select(ToUsageView)],
            PrUrl: prUrl,
            UnreadablePullRequestLink: unreadableLink,
            StartedBy: run.StartedBy?.Id,
            WriteAudit: [.. writes.Select(ToWriteAuditView)]);
    }

    private static NodeUsageView ToUsageView(WorkflowRunEvent completion)
    {
        var usage = completion.Usage!.Value;
        return new NodeUsageView(
            completion.Seq, completion.FromNode!, usage.InputTokens, usage.OutputTokens,
            usage.CacheReadTokens, usage.CacheWriteTokens, usage.ModelId ?? "");
    }

    /// <summary>
    ///     The run's <c>pr_url</c> variable as a link, or, when it is not an absolute http or https URL, as the text it
    ///     holds; both <see langword="null"/> when the run has none yet. <c>OpenPullRequestAction</c> is its only writer
    ///     and writes only such a URL, and no agent node can write it, but a value that does not read as one is still
    ///     reported as data on the view rather than failing the request: the view is what a human at the gate reads.
    /// </summary>
    private static (Uri? Link, string? Unreadable) ReadPrUrl(WorkflowRun run)
    {
        if (!run.Variables.TryGetValue(ReviewHandoff.PrUrlKey, out var value) || value?.ToString() is not { } text)
        {
            return (null, null);
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var link)
            && (string.Equals(link.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || string.Equals(link.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
            ? (link, null)
            : (null, text);
    }

    /// <summary>
    ///     Reads one write-audit record. Its payload is <c>{ tool, path }</c> as <c>AuditingToolAuthorizer</c> wrote
    ///     it, read as parsed JSON, since <c>jsonb</c> keeps the value and not the text.
    /// </summary>
    private static WriteAuditView ToWriteAuditView(WorkflowRunRecord record)
    {
        using var payload = JsonDocument.Parse(record.PayloadJson);
        var root = payload.RootElement;
        return new WriteAuditView(
            record.Seq, record.Node, ReadString(root, "tool") ?? "", ReadString(root, "path"), record.StartedById);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    ///     Resumes <paramref name="id"/> if — and only if — it is parked awaiting exactly
    ///     <paramref name="request"/>'s <see cref="ResumeWorkflowRunRequest.Signal"/>. 404 when the run does not
    ///     exist — the same status <see cref="Cancel"/> uses for the same case, so a caller does not have to
    ///     learn two conventions for "no such run" on one controller. A mismatched or absent signal fails with
    ///     409 and a message naming what the run is actually awaiting; it never silently no-ops the run's status.
    ///     <see cref="ResumeWorkflowRunRequest.ApplyStandingInstructions"/>, task B5's own addition, maps
    ///     <see cref="ResumeRefusal.WriteFailed"/> to 500 — a filesystem fault, not a bad request — and every
    ///     other <see cref="ResumeRefusal"/> to the same 409 an engine-level mismatch already used.
    /// </summary>
    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Resume(Guid id, [FromBody] ResumeWorkflowRunRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Signal))
        {
            return Problem(detail: "Signal must not be blank.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (await runs.FindAsync(id, ct) is null)
        {
            return NotFound();
        }

        // After the 404 check, not before it: a resume of a run that does not exist is a 404 whoever asks.
        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        var approver = RunPrincipals.From(caller, User.FindFirst("preferred_username")?.Value);
        var result = await runs.ResumeAsync(id, request.Signal, request.Payload, request.ApplyStandingInstructions, approver, ct);
        if (result.IsSuccess)
        {
            return NoContent();
        }

        var statusCode = result.Error.Kind == ResumeRefusal.WriteFailed
            ? StatusCodes.Status500InternalServerError
            : StatusCodes.Status409Conflict;
        return Problem(detail: result.Error.Detail, statusCode: statusCode);
    }

    /// <summary>
    ///     Cancels <paramref name="id"/> for <paramref name="request"/>'s reason, before it reaches a terminal
    ///     node. 404 when the run does not exist. <see cref="IWorkflowStore.CancelAsync"/> throws
    ///     <see cref="InvalidOperationException"/> for the same case and <see cref="WorkflowConcurrencyException"/>
    ///     when another write already changed the run between the lookup above and the write below — both are
    ///     caught here rather than left to become an unhandled 500, since a lost race is an ordinary outcome for
    ///     a run under concurrent operator action, not a server fault.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelWorkflowRunRequest? request, CancellationToken ct)
    {
        if (await runs.FindAsync(id, ct) is null)
        {
            return NotFound();
        }

        try
        {
            await runs.CancelAsync(id, request?.Reason ?? "Cancelled via API", ct);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (WorkflowConcurrencyException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        return NoContent();
    }

    /// <summary>
    ///     Re-runs the host-action node <paramref name="id"/> failed at, such as <c>publish</c> after a push was
    ///     refused. Admin only, narrower than the controller's <c>WorkflowResume</c> policy: it pushes and opens a
    ///     PR on a run another person approved. 404 when the run does not exist. 409 when the store refuses: not
    ///     Failed, not at a host-action node, or changed since it was read.
    /// </summary>
    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        if (await runs.FindAsync(id, ct) is null)
        {
            return NotFound();
        }

        if (!HttpSecurityContextFactory.TryCreate(User, out var caller))
        {
            return Unauthorized();
        }

        var retriedBy = RunPrincipals.From(caller, User.FindFirst("preferred_username")?.Value);
        var result = await runs.RetryAsync(id, retriedBy, ct);
        return result.IsSuccess ? NoContent() : Problem(detail: result.Error, statusCode: StatusCodes.Status409Conflict);
    }
}

/// <summary>Request body for <see cref="WorkflowRunsController.Resume"/>.</summary>
/// <param name="Signal">The signal the caller believes the run is parked awaiting.</param>
/// <param name="Payload">Carried into <see cref="WorkflowRun.Variables"/>["payload"] by <see cref="Thalos.Workflow.IWorkflowStore.ResumeAsync"/>.</param>
/// <param name="ApplyStandingInstructions">
///     Task B5. Defaults to <see langword="false"/>, so every existing caller of this endpoint is unaffected.
///     Set <see langword="true"/> only when a human operator has decided to accept the run's proposed standing
///     instructions — this is the one flag that makes <see cref="WorkflowRunGateway"/> write to disk at all.
/// </param>
public sealed record ResumeWorkflowRunRequest(string Signal, string? Payload, bool ApplyStandingInstructions = false);

/// <summary>Request body for <see cref="WorkflowRunsController.Cancel"/>.</summary>
/// <param name="Reason">A human-readable reason recorded against the run; defaults when omitted.</param>
public sealed record CancelWorkflowRunRequest(string? Reason);

/// <summary>Request body for <see cref="WorkflowRunsController.Start"/>.</summary>
/// <param name="WorkIntent">What the run should manufacture, in the requester's own words. Must not be blank.</param>
/// <param name="Repository">
///     The name of an entry in <c>Thalos:Workflow:Repositories</c>, such as <c>sandbox</c>. Must not be blank. Only a
///     name, never a URL: a name that is not allow-listed is a 400, so no request can point a run at another remote.
/// </param>
public sealed record StartWorkflowRunRequest(string WorkIntent, string Repository);

/// <summary>Response body for <see cref="WorkflowRunsController.Start"/>.</summary>
/// <param name="RunId">The id of the run that was just started.</param>
public sealed record StartWorkflowRunResponse(Guid RunId);

/// <summary>Response body for <see cref="WorkflowRunsController.Get"/>.</summary>
/// <param name="Id">The run's identity.</param>
/// <param name="Process">The process name this run is executing.</param>
/// <param name="ProcessVersion">The version of the process this run is executing.</param>
/// <param name="Status">The run's lifecycle status (<see cref="Thalos.Workflow.WorkflowStatus"/>, as text).</param>
/// <param name="CurrentNode">The node the run is currently positioned at.</param>
/// <param name="AwaitingSignal">The signal a parked run is waiting on, or <see langword="null"/> otherwise.</param>
/// <param name="LastError">The most recent error recorded against this run, or <see langword="null"/> if it has not failed.</param>
/// <param name="Pins">
///     The run's write-once agent/skill pins, keyed by task node name, or <see langword="null"/> for a run started
///     before manifests existed or through the legacy positional <c>StartAsync</c> overload.
/// </param>
/// <param name="StandingInstructionsDiff">
///     A unified-style line diff of the run's pinned standing instructions against its <c>retrospect</c>
///     proposal (see <see cref="Daedalus.Agents.Workflow.StandingInstructionsWriter.Diff"/>), or
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
/// <param name="WriteAudit">Every workspace write the run was allowed, from its write-audit records, in seq order.</param>
public sealed record WorkflowRunView(
    Guid Id,
    string Process,
    int ProcessVersion,
    string Status,
    string CurrentNode,
    string? AwaitingSignal,
    string? LastError,
    IReadOnlyDictionary<string, NodePin>? Pins,
    string? StandingInstructionsDiff,
    IReadOnlyList<NodeUsageView> NodeUsage,
    Uri? PrUrl,
    string? UnreadablePullRequestLink,
    string? StartedBy,
    IReadOnlyList<WriteAuditView> WriteAudit);

/// <summary>The token usage one completed agent node reported, read off its completion event.</summary>
/// <param name="Seq">The seq of the node execution the completion event closes.</param>
/// <param name="Node">The node that completed.</param>
/// <param name="InputTokens">Input tokens, cache reads and writes included.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CacheReadTokens">Input tokens read from a prompt cache.</param>
/// <param name="CacheWriteTokens">Input tokens written to a prompt cache.</param>
/// <param name="ModelId">The model that served the node, or empty when the turn reported none.</param>
public sealed record NodeUsageView(long Seq, string Node, int InputTokens, int OutputTokens, int CacheReadTokens, int CacheWriteTokens, string ModelId);

/// <summary>One workspace write the tool authorizer allowed and recorded, read off the run's write-audit records.</summary>
/// <param name="Seq">The run's seq when the write was allowed.</param>
/// <param name="Node">The node the run was on.</param>
/// <param name="Tool">The qualified name of the write tool.</param>
/// <param name="Path">The path the call named, or <see langword="null"/> when it named none.</param>
/// <param name="StartedBy">The id of the principal that started the run, or <see langword="null"/> when it carries none.</param>
public sealed record WriteAuditView(long Seq, string Node, string Tool, string? Path, string? StartedBy);
