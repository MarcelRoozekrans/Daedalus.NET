using System.Globalization;
using System.Text.Json;
using Daedalus.Agents.Security;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Agents;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Domain.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Thalos.Workflow;
using RepoRef = Daedalus.Infrastructure.Services.GitHub.RepoRef;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     Starts, lists, resumes, cancels, retries and reads back manufacture runs. Every route requires the
///     <c>WorkflowResume</c> authorization policy (<c>Program.cs</c>: <c>developer</c> or <c>admin</c> role), the same
///     criterion <c>Thalos:ToolPolicies</c> binds <c>git__*</c>, <c>repoaction__*</c> and <c>manufacture__*</c> to. It is
///     enforced here by ASP.NET Core's own role-based authorization rather than <c>DefaultToolAuthorizer</c>, because
///     these are REST endpoints, not Thalos tool calls, and <c>DefaultToolAuthorizer</c> never sees a request that never
///     names a tool. Starting a run over REST and starting one through <c>manufacture__start</c> are deliberately gated
///     the same way, by two different mechanisms that happen to require the same role. Retry is narrower than the rest:
///     it re-runs a push on a run another person approved, so <see cref="Retry"/> additionally requires the narrower
///     <c>Admin</c> policy.
/// </summary>
/// <remarks>
///     <para>
///     <b>Resume, cancel and retry are deliberately not Thalos tools.</b> Resuming a gate is reachable only through these
///     endpoints. No agent — including one running as the run's own <c>WorkflowCaller</c> — can call them, because they
///     are never registered as a local tool source and therefore never appear in any agent's resolved tool list. An
///     agent that could resume its own approval gate would make every gate in the engine decorative. The
///     <c>WorkflowResume</c> policy above is a separate layer, for a separate caller shape: a human, or a scheduled run's
///     HTTP caller, presenting roles over a real <c>ClaimsPrincipal</c>. <c>WorkflowCaller</c> is never one of those —
///     it is a Thalos <c>ISecurityContext</c> an agent turn carries internally, never something that reaches this
///     group's authorization pipeline at all — so this policy neither denies nor could deny it; the tool-source absence
///     above is what actually stops it.
///     </para>
///     <para>
///     <b>Starting is different: it is also a Thalos tool.</b> <c>manufacture__start</c>
///     (<see cref="Daedalus.Agents.Tools.DaedalusManufactureTools"/>) calls the very same
///     <see cref="IManufactureRunStarter"/> <see cref="Start"/> does — starting a run is not the same hazard resuming one
///     is, so it is deliberately reachable both ways, with the tool path gated at the authorizer by
///     <c>Thalos:ToolPolicies</c> instead of by absence.
///     </para>
///     <para>
///     <b>Ported from the MVC controller of <c>api/workflow-runs</c></b> on the pattern of <see cref="TasksEndpoints"/>
///     (R42, R45, R53). The group carries <see cref="WorkflowEngineEnabledEndpointFilter"/> through
///     <see cref="DaedalusEndpoints.MapApiGroup"/>, and the handlers resolve <see cref="WorkflowRunGateway"/> and
///     <see cref="IWorkflowRunQuery"/> from <see cref="HttpContext.RequestServices"/>, never as parameters: only a host
///     with the engine on registers them, and a parameter is resolved before the filter runs, so a disabled host would
///     answer a bare 500 instead of the filter's 503. The controller had <c>[Produces("application/json")]</c>, so the
///     group carries <see cref="ProblemContentType.Json"/>; every <c>Problem</c>, <c>NotFound()</c> and
///     <c>Unauthorized()</c> goes out through <see cref="ApiProblem.Of"/>. There is no catch-all and no logging, as the
///     controller had none.
///     </para>
/// </remarks>
public static class WorkflowRunsEndpoints
{
    /// <summary>The page size when <c>take</c> is not sent.</summary>
    private const int DefaultTake = 25;

    /// <summary>
    ///     The ISO 8601 forms <c>updatedSince</c> takes, each with its zone: <c>Z</c> or an offset. The fraction of a
    ///     second, up to seven digits, is optional, so whole seconds and the round-trip "O" form both parse. A loose form
    ///     such as <c>10/08/2026</c>, which reads as two different days by culture, is refused.
    /// </summary>
    private static readonly string[] UpdatedSinceFormats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];

    /// <summary>
    ///     Maps <c>api/workflow-runs</c> behind the workflow-engine filter, with the <c>WorkflowResume</c> policy on every
    ///     route and <c>Admin</c> added on retry.
    /// </summary>
    public static RouteGroupBuilder MapWorkflowRunsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/workflow-runs", requiresWorkflowEngine: true)
            .RequireAuthorization("WorkflowResume")
            .WithTags("WorkflowRuns")
            .WithMetadata(ProblemContentType.Json);

        group.MapPost(string.Empty, Start)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status401Unauthorized, "application/json")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity, "application/json")
            .ProducesProblem(StatusCodes.Status500InternalServerError, "application/json")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable, "application/json");

        group.MapGet(string.Empty, List)
            .WithQueryParameter<string>("status")
            .WithQueryParameter<string>("process")
            .WithQueryParameter<string>("after")
            .WithQueryParameter<int>("take")
            .WithQueryParameter<string>("signal")
            .WithQueryParameter<string>("updatedSince")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json");

        group.MapGet("{id:guid}", Get)
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json");

        group.MapPost("{id:guid}/resume", Resume)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status401Unauthorized, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity, "application/json")
            .ProducesProblem(StatusCodes.Status500InternalServerError, "application/json");

        group.MapPost("{id:guid}/cancel", Cancel)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        group.MapPost("{id:guid}/retry", Retry)
            .RequireAuthorization("Admin")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized, "application/json")
            .ProducesProblem(StatusCodes.Status403Forbidden, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        return group;
    }

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
    /// <param name="context">The request, for its user and the problem's content type.</param>
    /// <param name="starter">
    ///     A parameter, unlike the gateway: every host registers an <see cref="IManufactureRunStarter"/>, the disabled
    ///     one included, so resolving it ahead of the engine filter cannot fail.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Results<Created<StartWorkflowRunResponse>, ApiProblemResult>> Start(
        ApiBody<StartWorkflowRunRequest> request, HttpContext context, [FromServices] IManufactureRunStarter starter,
        CancellationToken ct)
    {
        var body = request.Value!;
        if (string.IsNullOrWhiteSpace(body.WorkIntent))
        {
            return ApiProblem.Of(context, StatusCodes.Status400BadRequest, "WorkIntent must not be blank.");
        }

        if (string.IsNullOrWhiteSpace(body.Repository))
        {
            return ApiProblem.Of(context, StatusCodes.Status400BadRequest, "Repository must not be blank.");
        }

        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        var startRequest = new ManufactureStartRequest(
            body.WorkIntent, body.Repository, RunPrincipals.From(caller, context.User.FindFirst("preferred_username")?.Value),
            body.Process, body.BaseBranch);
        var result = await starter.StartAsync(startRequest, ct);
        if (result.IsFailure)
        {
            return ManufactureStartProblem.From(context, result.Error);
        }

        return TypedResults.Created(
            ApiLocation.For(context.Request, $"/api/workflow-runs/{result.Value}"), new StartWorkflowRunResponse(result.Value));
    }

    /// <summary>
    ///     Runs newest first, one page at a time, with the board task each belongs to. Never a run's variables or its
    ///     error. 400 for an unknown status, a take outside 1 to 100, a cursor this endpoint did not write, or an
    ///     <paramref name="updatedSince"/> that is not a timestamp.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <paramref name="signal"/> keeps the runs awaiting that signal, and <paramref name="updatedSince"/> the runs that
    ///     last changed at or after that instant. The store filters only on status and process, so with either of them
    ///     the list reads the store page by page, keeps the matches, and stops at <paramref name="take"/> matches, at the
    ///     end of the store, or after <see cref="RunListLimits.ScanCap"/> summaries. <c>Next</c> is set only when more
    ///     matches may remain: <c>take</c> was reached before the end of the store, or the cap was.
    ///     </para>
    ///     <para>
    ///     The string values bind as absent when sent empty or blank, as MVC's <c>ConvertEmptyStringToNull</c> bound
    ///     them null, so <see cref="ApiQuery{T}.HasValue"/> is the controller's blank check. <paramref name="take"/> is
    ///     optional, so it binds an empty value as absent, as MVC bound the controller's <c>int? take</c> null; it is
    ///     last because an optional parameter must be (R53).
    ///     </para>
    /// </remarks>
    public static async Task<Results<Ok<WorkflowRunList>, ApiProblemResult>> List(
        ApiQuery<string> status, ApiQuery<string> process, ApiQuery<string> after, ApiQuery<string> signal,
        ApiQuery<string> updatedSince, HttpContext context, [FromServices] ITaskQueryService tasks, CancellationToken ct,
        ApiQuery<int>? take = null)
    {
        WorkflowStatus? statusFilter = null;
        if (status.HasValue)
        {
            statusFilter = ParseStatusName(status.Value);
            if (statusFilter is null)
            {
                return ApiProblem.Of(
                    context, StatusCodes.Status400BadRequest,
                    $"status must be one of {string.Join(", ", Enum.GetNames<WorkflowStatus>())}.");
            }
        }

        var pageSize = take?.ValueOr(DefaultTake) ?? DefaultTake;
        if (pageSize is < 1 or > WorkflowRunFilter.MaxTake)
        {
            return ApiProblem.Of(context, StatusCodes.Status400BadRequest, "take must be between 1 and 100.");
        }

        WorkflowRunCursor? cursor = null;
        if (after.HasValue)
        {
            if (!WorkflowRunCursorCodec.TryDecode(after.Value, out var decoded))
            {
                return ApiProblem.Of(context, StatusCodes.Status400BadRequest, "after is not a cursor this list returned.");
            }

            cursor = decoded;
        }

        DateTimeOffset? since = null;
        if (updatedSince.HasValue)
        {
            if (!DateTimeOffset.TryParseExact(
                    updatedSince.Value, UpdatedSinceFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return ApiProblem.Of(
                    context, StatusCodes.Status400BadRequest,
                    "updatedSince must be an ISO 8601 timestamp with its zone, such as 2026-10-08T12:00:00Z.");
            }

            since = parsed;
        }

        var filter = new WorkflowRunFilter
        {
            Status = statusFilter,
            Process = process.Value,
            After = cursor,
            Take = pageSize,
        };
        var awaitingSignal = signal.Value;
        var query = context.RequestServices.GetRequiredService<IWorkflowRunQuery>();
        var runs = context.RequestServices.GetRequiredService<WorkflowRunGateway>();
        var page = awaitingSignal is null && since is null
            ? await query.ListAsync(filter, ct).ConfigureAwait(false)
            : await ScanAsync(query, filter, run => Matches(run, awaitingSignal, since), ct).ConfigureAwait(false);
        var links = await tasks.GetRunLinksAsync([.. page.Runs.Select(r => r.Id)], ct).ConfigureAwait(false);

        var items = new List<WorkflowRunListItem>(page.Runs.Count);
        foreach (var summary in page.Runs)
        {
            items.Add(await ToListItemAsync(runs, summary, links.GetValueOrDefault(summary.Id), ct).ConfigureAwait(false));
        }

        return TypedResults.Ok(new WorkflowRunList(
            items,
            page.Next is null ? null : WorkflowRunCursorCodec.Encode(page.Next)));
    }

    /// <summary>Reads back one run's current state, including its write-once manifest pins when it has one.</summary>
    public static async Task<Results<Ok<WorkflowRunView>, ApiProblemResult>> Get(Guid id, HttpContext context, CancellationToken ct)
    {
        var runs = context.RequestServices.GetRequiredService<WorkflowRunGateway>();
        var run = await runs.FindAsync(id, ct);
        return run is null
            ? ApiProblem.Of(context, StatusCodes.Status404NotFound)
            : TypedResults.Ok(await ToViewAsync(runs, run, ct));
    }

    /// <summary>
    ///     Resumes <paramref name="id"/> if — and only if — it is parked awaiting exactly
    ///     <paramref name="request"/>'s <see cref="ResumeWorkflowRunRequest.Signal"/>. 404 when the run does not
    ///     exist — the same status <see cref="Cancel"/> uses for the same case, so a caller does not have to
    ///     learn two conventions for "no such run" on one group. A mismatched or absent signal fails with
    ///     409 and a message naming what the run is actually awaiting; it never silently no-ops the run's status.
    ///     <see cref="ResumeWorkflowRunRequest.ApplyStandingInstructions"/>, task B5's own addition, maps
    ///     <see cref="ResumeRefusal.WriteFailed"/> to 500, a filesystem fault and not a bad request;
    ///     <see cref="ResumeRefusal.PublishRefused"/> to 422 (ruling R57: in sandbox mode the publish-side protected-path
    ///     check runs inside this request, and its refusal, whose detail names the refused path, is a policy refusal as
    ///     R50's pin refusals are), and every other <see cref="ResumeRefusal"/> to the same 409 an engine-level mismatch
    ///     already used.
    /// </summary>
    public static async Task<Results<NoContent, ApiProblemResult>> Resume(
        Guid id, ApiBody<ResumeWorkflowRunRequest> request, HttpContext context, CancellationToken ct)
    {
        var body = request.Value!;
        if (string.IsNullOrWhiteSpace(body.Signal))
        {
            return ApiProblem.Of(context, StatusCodes.Status400BadRequest, "Signal must not be blank.");
        }

        var runs = context.RequestServices.GetRequiredService<WorkflowRunGateway>();
        if (await runs.FindAsync(id, ct) is null)
        {
            return ApiProblem.Of(context, StatusCodes.Status404NotFound);
        }

        // After the 404 check, not before it: a resume of a run that does not exist is a 404 whoever asks.
        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        var approver = RunPrincipals.From(caller, context.User.FindFirst("preferred_username")?.Value);
        var result = await runs.ResumeAsync(id, body.Signal, body.Payload, body.ApplyStandingInstructions, body.DropFindings, approver, ct);
        if (result.IsSuccess)
        {
            return TypedResults.NoContent();
        }

        var statusCode = result.Error.Kind switch
        {
            ResumeRefusal.WriteFailed => StatusCodes.Status500InternalServerError,
            ResumeRefusal.PublishRefused => StatusCodes.Status422UnprocessableEntity,
            ResumeRefusal.UnknownFinding => StatusCodes.Status422UnprocessableEntity,
            ResumeRefusal.RecordFailed => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status409Conflict,
        };
        return ApiProblem.Of(context, statusCode, result.Error.Detail);
    }

    /// <summary>
    ///     Cancels <paramref name="id"/> for <paramref name="request"/>'s reason, before it reaches a terminal
    ///     node. 404 when the run does not exist. <see cref="IWorkflowStore.CancelAsync"/> throws
    ///     <see cref="InvalidOperationException"/> for the same case and <see cref="WorkflowConcurrencyException"/>
    ///     when another write already changed the run between the lookup above and the write below — both are
    ///     caught here rather than left to become an unhandled 500, since a lost race is an ordinary outcome for
    ///     a run under concurrent operator action, not a server fault.
    /// </summary>
    /// <remarks>
    ///     The body is optional, as the controller's nullable <c>CancelWorkflowRunRequest?</c> was: an empty body binds
    ///     <see langword="null"/> (R17). An optional parameter must come last, so it follows <paramref name="ct"/>.
    /// </remarks>
    public static async Task<Results<NoContent, ApiProblemResult>> Cancel(
        Guid id, HttpContext context, CancellationToken ct, ApiBody<CancelWorkflowRunRequest>? request = null)
    {
        var runs = context.RequestServices.GetRequiredService<WorkflowRunGateway>();
        if (await runs.FindAsync(id, ct) is null)
        {
            return ApiProblem.Of(context, StatusCodes.Status404NotFound);
        }

        try
        {
            await runs.CancelAsync(id, request?.Value?.Reason ?? "Cancelled via API", ct);
        }
        catch (InvalidOperationException)
        {
            return ApiProblem.Of(context, StatusCodes.Status404NotFound);
        }
        catch (WorkflowConcurrencyException ex)
        {
            return ApiProblem.Of(context, StatusCodes.Status409Conflict, ex.Message);
        }

        return TypedResults.NoContent();
    }

    /// <summary>
    ///     Re-runs the host-action node <paramref name="id"/> failed at, such as <c>publish</c> after a push was
    ///     refused. Admin only, narrower than the group's <c>WorkflowResume</c> policy: it pushes and opens a
    ///     PR on a run another person approved. 404 when the run does not exist. 409 when the store refuses: not
    ///     Failed, not at a host-action node, or changed since it was read.
    /// </summary>
    public static async Task<Results<NoContent, ApiProblemResult>> Retry(Guid id, HttpContext context, CancellationToken ct)
    {
        var runs = context.RequestServices.GetRequiredService<WorkflowRunGateway>();
        if (await runs.FindAsync(id, ct) is null)
        {
            return ApiProblem.Of(context, StatusCodes.Status404NotFound);
        }

        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        var retriedBy = RunPrincipals.From(caller, context.User.FindFirst("preferred_username")?.Value);
        var result = await runs.RetryAsync(id, retriedBy, ct);
        return result.IsSuccess
            ? TypedResults.NoContent()
            : ApiProblem.Of(context, StatusCodes.Status409Conflict, result.Error);
    }

    /// <summary>Maps a Thalos pin to the Thalos-free view. The agent id is its ULID text, which is how Thalos serializes it.</summary>
    internal static NodePinView ToPinView(NodePin pin) =>
        new(pin.AgentName, pin.AgentId.ToString(), pin.AgentRevision, pin.SkillName, pin.SkillHash);

    private static bool Matches(WorkflowRunSummary run, string? signal, DateTimeOffset? since) =>
        (signal is null || string.Equals(run.AwaitingSignal, signal, StringComparison.Ordinal))
        && (since is null || run.UpdatedAt >= since);

    /// <summary>
    ///     Reads <paramref name="filter"/>'s runs from the store, newest first, keeping those <paramref name="matches"/>
    ///     accepts, until <see cref="WorkflowRunFilter.Take"/> are kept, the store has no more, or
    ///     <see cref="RunListLimits.ScanCap"/> summaries were read. The cursor names the last summary read, so the next page
    ///     goes on from there; it is null only when the store has no more.
    /// </summary>
    private static async Task<WorkflowRunPage> ScanAsync(
        IWorkflowRunQuery query, WorkflowRunFilter filter, Func<WorkflowRunSummary, bool> matches, CancellationToken ct)
    {
        var kept = new List<WorkflowRunSummary>(filter.Take);
        var after = filter.After;
        var scanned = 0;
        while (scanned < RunListLimits.ScanCap)
        {
            var page = await query.ListAsync(
                    new WorkflowRunFilter
                    {
                        Status = filter.Status,
                        Process = filter.Process,
                        After = after,
                        Take = Math.Min(WorkflowRunFilter.MaxTake, RunListLimits.ScanCap - scanned),
                    },
                    ct)
                .ConfigureAwait(false);
            for (var i = 0; i < page.Runs.Count; i++)
            {
                var run = page.Runs[i];
                scanned++;
                if (!matches(run))
                {
                    continue;
                }

                kept.Add(run);
                if (kept.Count == filter.Take)
                {
                    var last = i == page.Runs.Count - 1 && page.Next is null;
                    return new WorkflowRunPage(kept, last ? null : new WorkflowRunCursor(run.CreatedAt, run.Id));
                }
            }

            // An empty page ends the scan whatever its cursor says, so a store that breaks its contract cannot loop it.
            if (page.Next is null || page.Runs.Count == 0)
            {
                return new WorkflowRunPage(kept, null);
            }

            after = page.Next;
        }

        return new WorkflowRunPage(kept, after);
    }

    /// <summary>
    ///     The <see cref="WorkflowStatus"/> member <paramref name="name"/> names, ignoring case, or <see langword="null"/>.
    ///     Names only: <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would also take a number such as
    ///     <c>1</c> and a comma list such as <c>Awaiting,Succeeded</c>, which it ORs into another member.
    /// </summary>
    private static WorkflowStatus? ParseStatusName(string name)
    {
        foreach (var member in Enum.GetValues<WorkflowStatus>())
        {
            if (string.Equals(member.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                return member;
            }
        }

        return null;
    }

    /// <summary>
    ///     One list row. The summary carries neither the run's variables nor its error, so the run is read for its pull
    ///     request link, through the same <see cref="ReadPrUrl"/> the view uses. Only a <c>Failed</c> run reads its steps and,
    ///     when the review limit stopped it, its review evidence, so a page of healthy runs costs one lookup per row. A batch
    ///     read of the link would drop that lookup. See #367.
    /// </summary>
    private static async Task<WorkflowRunListItem> ToListItemAsync(
        WorkflowRunGateway runs, WorkflowRunSummary summary, TaskRunLink? task, CancellationToken ct)
    {
        var run = await runs.FindAsync(summary.Id, ct).ConfigureAwait(false);
        var prUrl = run is null ? null : ReadPrUrl(run).Link;
        string? reason = null;
        if (summary.Status == WorkflowStatus.Failed)
        {
            var steps = WorkflowRunStepProjection.Project(await runs.ListEventsAsync(summary.Id, ct).ConfigureAwait(false));
            reason = RunFailureReason.Derive(
                summary.Status.ToString(), steps, run?.LastError, await RejectingLensAsync(runs, summary.Id, steps, ct).ConfigureAwait(false));
        }

        return new WorkflowRunListItem(
            summary.Id, summary.Process, summary.ProcessVersion, summary.Status.ToString(), summary.CurrentNode, summary.AwaitingSignal,
            summary.StartedBy?.Id, summary.CreatedAt, summary.UpdatedAt, task?.TaskId, task?.TaskCode, task?.Title, summary.StartedBy?.DisplayName,
            reason, prUrl);
    }

    /// <summary>The lens of the latest rejected review visit, read only when the steps end in the review limit; null when unreadable.</summary>
    private static async Task<string?> RejectingLensAsync(
        WorkflowRunGateway runs, Guid runId, IReadOnlyList<WorkflowRunStepView> steps, CancellationToken ct)
    {
        if (!RunFailureReason.IsCappedReview(steps))
        {
            return null;
        }

        var review = await runs.ReadReviewAsync(runId, ct).ConfigureAwait(false);
        return review.IsSuccess ? review.Value?.RejectedLens : null;
    }

    /// <summary>
    ///     Builds the view. Node usage comes from the run's completion events, never from its variables, which no
    ///     node's usage is written to. The write audit comes from the host-written records, which no agent can add
    ///     to or change.
    /// </summary>
    private static async Task<WorkflowRunView> ToViewAsync(WorkflowRunGateway runs, WorkflowRun run, CancellationToken ct)
    {
        var events = await runs.ListEventsAsync(run.Id, ct);
        var writes = await runs.ListRecordsAsync(run.Id, WorkflowRunRecord.WorkspaceWriteKind, ct);
        var selections = await runs.ListRecordsAsync(run.Id, WorkflowRunRecord.NodeSelectionKind, ct);
        var review = await runs.ReadReviewAsync(run.Id, ct);
        var (reviewVisits, reviewVisitsError) = ReviewVisitHistory.Read(
            await runs.ListRecordsAsync(run.Id, WorkflowRunRecord.ReviewEvidenceKind, ct),
            await runs.ListRecordsAsync(run.Id, WorkflowRunRecord.ReviewResponseKind, ct));
        IReadOnlyList<IdentifiedDeferredFinding> deferred = review.IsSuccess ? ReviewVisit.DeferredOf(review.Value) : [];
        var drops = await runs.ListRecordsAsync(run.Id, WorkflowRunRecord.FindingsDroppedKind, ct);
        var voids = await runs.ListRecordsAsync(run.Id, WorkflowRunRecord.FindingsDropVoidedKind, ct);
        var filed = await runs.ListFiledFindingsAsync(run.Id, ct);
        var dropped = FindingRecords.ReadDropped([.. drops, .. voids]);
        var droppedIds = dropped.IsSuccess ? dropped.Value.Ids : DroppedFindings.None.Ids;
        var error = string.Join("; ", new[] { review.IsFailure ? review.Error : null, dropped.IsFailure ? dropped.Error : null, filed.IsFailure ? filed.Error : null }.Where(e => e is not null));

        // The shared parser NodeSelectionRunner reads with. The tool filter is read by it but never put in the view.
        var readSelections = selections.OrderBy(r => r.Seq)
            .Select(r => (Record: r, Selection: NodeSelectionRecord.Parse(r.PayloadJson)))
            .Select(x => (x.Record, View: x.Selection is { } d
                ? new NodeSelectionView(x.Record.Seq, x.Record.Node, d.Model, d.Level, d.KeptFamilies, d.Fallback, d.Confidence)
                : null))
            .ToList();
        var unreadableSelections = readSelections.Where(x => x.View is null).Select(x => x.Record.Seq.ToString(CultureInfo.InvariantCulture)).ToList();

        var steps = WorkflowRunStepProjection.Project(events);
        var rejectingLens = review.IsSuccess ? review.Value?.RejectedLens : null;
        var (prUrl, unreadableLink) = ReadPrUrl(run);
        // The repository a deferred finding's existing issue number lives in. At the gate there is no pull request
        // yet: publish runs after it, so the run's workspace names the repository. Once a pull request exists,
        // its repository is the one file-review-findings resolved the number in, so it wins. The workspace is looked
        // up only when a deferred finding has an issue number to link.
        RepoRef? issueRepo = null;
        if (PullRequestLink.TryParse(prUrl, out var repo, out _))
        {
            issueRepo = repo;
        }
        else if (deferred.Any(d => d.Finding.ExistingIssue is not null))
        {
            issueRepo = await runs.FindRepositoryAsync(run.Id, ct);
        }
        return new WorkflowRunView(
            run.Id,
            run.Process,
            run.ProcessVersion,
            run.Status.ToString(),
            run.CurrentNode,
            run.AwaitingSignal,
            run.LastError,
            run.Manifest?.Nodes.ToDictionary(p => p.Key, p => ToPinView(p.Value), StringComparer.Ordinal),
            StandingInstructionsDiff: StandingInstructionsWriter.Diff(run),
            NodeUsage: [.. events.Where(e => e.Usage is not null && e.FromNode is not null).Select(ToUsageView)],
            PrUrl: prUrl,
            UnreadablePullRequestLink: unreadableLink,
            StartedBy: run.StartedBy?.Id,
            WriteAudit: [.. writes.Select(ToWriteAuditView)],
            DeferredFindings: [.. deferred.Select(d => ToDeferredView(d, droppedIds, issueRepo))],
            DeferredFindingsError: error.Length > 0 ? error : null,
            FiledFindings: filed.IsSuccess ? [.. filed.Value.Values.Order(FiledOrder.Instance).Select(f => new FiledFindingView(f.FindingId, f.Mode, f.Issue, f.Url))] : [],
            Steps: steps,
            StartedByName: run.StartedBy?.DisplayName,
            NodeSelections: [.. readSelections.Select(x => x.View).OfType<NodeSelectionView>()],
            NodeSelectionsError: unreadableSelections.Count > 0
                ? $"{unreadableSelections.Count} node-selection record(s) could not be read, at seq {string.Join(", ", unreadableSelections)}."
                : null,
            FailureReason: RunFailureReason.Derive(run.Status.ToString(), steps, run.LastError, rejectingLens),
            ReviewVisits: reviewVisits,
            ReviewVisitsError: reviewVisitsError);
    }

    /// <summary>
    ///     Maps one deferred finding. Its existing issue links into <paramref name="issueRepo"/>, the repository of
    ///     the run's pull request or, before there is one, of its workspace; with no repository there is no link, only
    ///     the number.
    /// </summary>
    private static DeferredFindingView ToDeferredView(IdentifiedDeferredFinding d, IReadOnlySet<string> dropped, RepoRef? issueRepo) =>
        new(d.Id, d.Lens, d.Finding.File, d.Finding.Line, d.Finding.Title, d.Finding.Scenario, d.Finding.Reason,
            d.Finding.ExistingIssue, dropped.Contains(d.Id),
            d.Finding.ExistingIssue is { } issue && issueRepo is not null ? issueRepo.IssueUrl(issue) : null);

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
    private static (Uri? Link, string? Unreadable) ReadPrUrl(WorkflowRun run) => PullRequestLink.Read(run);

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

    /// <summary>Findings in the order a human reads them: by lens, then by number (so 2 comes before 10), with the summary last.</summary>
    private sealed class FiledOrder : IComparer<FiledFinding>
    {
        public static readonly FiledOrder Instance = new();

        public int Compare(FiledFinding? x, FiledFinding? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            var summary = IsSummary(x).CompareTo(IsSummary(y));
            if (summary != 0)
                return summary;

            var (lensX, numberX) = Split(x.FindingId);
            var (lensY, numberY) = Split(y.FindingId);
            var lens = string.CompareOrdinal(lensX, lensY);
            return lens != 0 ? lens : numberX.CompareTo(numberY);
        }

        private static bool IsSummary(FiledFinding f) => string.Equals(f.FindingId, FindingRecords.SummaryId, StringComparison.Ordinal);

        private static (string Lens, int Number) Split(string id)
        {
            var dash = id.LastIndexOf('-');
            return dash > 0 && int.TryParse(id[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? (id[..dash], n)
                : (id, 0);
        }
    }
}
