using System.Text.Json;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Thalos.Git;
using Thalos.Git.Workspaces;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Task B13: the <c>open-pull-request</c> host action, run after the human gate. It commits the run's worktree in
///     two commits (the code, then the approved standing-instructions change), pushes the run's own branch, and opens
///     the pull request, or reuses the one already open for that branch. Host code does all of it; no agent holds a
///     tool that could.
/// </summary>
/// <remarks>
///     <para>
///     <b>Idempotent, because the outbox redelivers.</b> A commit with nothing staged makes no commit, a push of what the
///     remote already holds changes nothing, and the pull request is looked up by head branch through
///     <see cref="IOpenPullRequestLookup"/> before one is opened through <see cref="IPullRequestPublisher"/>. A
///     delivery that dies after opening the pull request is therefore redelivered to the same result, with one pull
///     request.
///     </para>
///     <para>
///     <b>The worktree is never removed here (rulings R14 and R19).</b> Removing it after the pull request would leave a
///     crash before the run's advance with a redelivery that finds no workspace and fails a run whose pull request is
///     open. <c>RunWorkspaceSweeper</c> removes it once the run is <c>Succeeded</c>; on a failure it stays for a human.
///     In local mode every failure message names it. A sandboxed run's message names no host path: its changes are kept
///     as the stored patch and the publish worktree cut from it, which a retry checks out again.
///     </para>
///     <para>
///     <b>Where it publishes from, and how it commits (phase 2.6, task B5).</b> The worktree comes from
///     <see cref="IRunWorkspaceHandoff.CheckoutForPublishAsync"/>. In local mode that is the run's own worktree, whose
///     agent wrote files that were never staged, so the code commit stages from disk as it always has. In sandbox mode
///     it is the trusted publish worktree into which Thalos applied the run's patch with <c>git apply --index</c>, and
///     that index is exactly what the publish-side protected-path check (S5) passed. The code commit then takes the
///     index as it stands, with <see cref="GitCommitRequest.CommitStagedIndex"/>: restaging from disk would drop an
///     added file the worktree's <c>.gitignore</c> matches and, under <c>core.fileMode=false</c>, a mode change, so
///     the commit would differ from what was checked. The standing-instructions commit is path-scoped in both modes, and
///     comes second, after the staged change is committed. The mode is <see cref="SandboxConfig.Enabled"/>, the same
///     flag that chooses which handoff the host registers, so the two cannot disagree.
///     </para>
///     <para>
///     <b>What the push already guarantees.</b> <see cref="IRunWorkspaceGit.PushAsync"/> checks that HEAD is the
///     workspace's own branch, refuses the default branch, pushes the explicit refspec straight to the workspace's
///     remote and writes no git config (A7). This action adds no second check, refspec or remote name.
///     </para>
///     <para>
///     <b>The <c>failed</c> outcome.</b> Two refusals are an outcome, which branches to <c>adjudicate</c>, rather than a
///     failure: a diff with nothing in it, and a run that records no review evidence at all. Both are decided before
///     anything is pushed, the second before anything is committed, and each says why in <c>publish_error</c>.
///     </para>
///     <para>
///     <b>Failures.</b> An expected failure is a failed <see cref="Result{T}"/>, which the dispatcher records prefixed
///     with the node's name. A collaborator answering success with nothing in it is a failure with a message, never a
///     null, and a pull request URL that is not an absolute http or https URL is a failure too, so the run's
///     <c>pr_url</c> is always a link. An <see cref="OperationCanceledException"/> propagates only when the dispatch's
///     own token was cancelled; one nobody asked for, such as an HTTP client's timeout, is a failed result, because the outbox would otherwise
///     retry it until the message is dead-lettered (see <see cref="IWorkflowHostAction"/>).
///     </para>
///     <para>
///     A singleton: the scoped <see cref="IOpenPullRequestLookup"/>, <see cref="IPullRequestPublisher"/> and the
///     record store are resolved from a scope of its own per call (ruling R28a).
///     </para>
/// </remarks>
internal sealed class OpenPullRequestAction(
    IRunWorkspaceHandoff handoff, IRunWorkspaceGit git, IServiceScopeFactory scopes, WorkflowConfig config) : IWorkflowHostAction
{
    /// <summary>The name a process node references with <c>action: open-pull-request</c>.</summary>
    public const string ActionName = ReviewHandoff.PublishActionName;

    /// <summary>What <see cref="ReviewHandoff.PublishErrorKey"/> says when the run records no review evidence at all.</summary>
    public const string NoReviewEvidence =
        "no review evidence is recorded for this run, and a pull request is opened only for a reviewed change";

    private readonly string _standingInstructionsPath = DaedalusAgentsServiceCollectionExtensions.StandingInstructionsRelativePath(
        (config ?? throw new ArgumentNullException(nameof(config))).StandingInstructionsPath);

    private readonly bool _sandboxed = config.Sandbox.Enabled;

    /// <inheritdoc />
    public string Name => ActionName;

    /// <inheritdoc />
    public async ValueTask<Result<HostActionResult>> RunAsync(WorkflowRun run, ProcessNode node, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Dispatch gates do not run before an action node, so a run with nothing to hand off is refused here (A13), and
        // so is a sandboxed run whose patch the publish-side protected-path check refused (S5): before any commit.
        var handedOff = await handoff.CheckoutForPublishAsync(run.Id, ct).ConfigureAwait(false);
        if (handedOff.IsFailure)
            return Result<HostActionResult>.Failure(Describe(handedOff.Error));

        var ws = handedOff.Value;

        try
        {
            return await PublishAsync(run, ws, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return Failed($"publishing was cancelled without the dispatch being cancelled, as a timeout does: {ex.Message}", ws);
        }
    }

    private async ValueTask<Result<HostActionResult>> PublishAsync(WorkflowRun run, RunWorkspace ws, CancellationToken ct)
    {
        // Thalos checks this action's variables against the run's key cap only after RunAsync returns, so a bag with no
        // room for them would fail the run after the push and the pull request. ReviewHandoffWorkflowStore keeps that
        // room from agent nodes; this refuses a run that has none anyway, before anything is committed.
        if (DescribeFullBag(run) is { } full)
            return Failed(full, ws);

        if (run.Manifest?.Documents.GetValueOrDefault(ManufactureRunStarter.WorkIntentDocument) is not { } workIntent
            || string.IsNullOrWhiteSpace(workIntent))
            return Failed($"run '{run.Id}' has no pinned work intent to describe the pull request with", ws);

        // Every refusal that does not depend on git comes before the first commit, so a refused run is never
        // committed or pushed, and a retry of one pushes nothing either. The repository must still be allow-listed, at
        // the remote the workspace was created for: the lookup below queries the configured remote (ruling R22), so
        // it must be the one the branch is pushed to.
        if (config.Repositories.FirstOrDefault(r => string.Equals(r.Name, ws.Repository, StringComparison.Ordinal)) is not { } repository)
            return Failed($"repository '{ws.Repository}' is no longer allow-listed", ws);
        if (!string.Equals(repository.Remote, ws.Remote, StringComparison.Ordinal))
            return Failed($"repository '{ws.Repository}' is now configured at a different remote than the run's workspace", ws);

        // Two interfaces (ruling R28a): IOpenPullRequestLookup reads, IPullRequestPublisher opens. Both are scoped, so
        // they come from this scope rather than this singleton's constructor, as does the record store.
        await using var scope = scopes.CreateAsyncScope();

        // The review evidence is read and validated before any side effect too, even on a redelivery whose pull
        // request is already open: it is one query against the host's own append-only store, and reading it first
        // means an unreadable record fails the run once, instead of every retry pushing and then failing.
        var reviewed = await CheckedAsync(run.Id, scope.ServiceProvider, ct).ConfigureAwait(false);
        if (reviewed.IsFailure)
            return Failed(reviewed.Error, ws);

        // Under process v6 publish is reached only through an approving review, which records its evidence. A run that
        // records none was not reviewed the way the pull request body would claim, so nothing is committed or pushed.
        if (reviewed.Value.Count == 0)
            return Outcome("failed", ReviewHandoff.PublishErrorKey, NoReviewEvidence);

        // The last test run the run's sandbox reported, read before any side effect like the evidence above, so an
        // unreadable store fails the run once rather than after a push.
        var tested = await LastTestResultAsync(run.Id, scope.ServiceProvider, ct).ConfigureAwait(false);
        if (tested.IsFailure)
            return Failed(tested.Error, ws);

        var author = new GitAuthor(config.CommitAuthor.Name, config.CommitAuthor.Email);

        // 1. Commit. Each call is a no-op when nothing is staged, so a redelivery commits nothing twice. In sandbox mode
        // the code commit takes the checked index as it stands; see the remarks. The standing-instructions file is
        // excluded either way, and committed on its own, path-scoped, second: its commit resets the index to HEAD first,
        // which after the code commit drops nothing.
        var code = await git.CommitAsync(
            ws,
            new GitCommitRequest
            {
                Message = PullRequestBody.CodeCommitMessage(workIntent, run.Id),
                Author = author,
                ExcludePaths = [_standingInstructionsPath],
                CommitStagedIndex = _sandboxed,
            },
            ct).ConfigureAwait(false);
        if (code.IsFailure)
            return Failed($"commit failed: {Describe(code.Error)}", ws);

        var standing = await git.CommitAsync(
            ws,
            new GitCommitRequest
            {
                Message = "docs: apply the approved standing instructions change",
                Author = author,
                Paths = [_standingInstructionsPath],
            },
            ct).ConfigureAwait(false);
        if (standing.IsFailure)
            return Failed($"{_standingInstructionsPath} commit failed: {Describe(standing.Error)}", ws);

        // Against the merge base of ws.BaseRef.
        var stat = await git.DiffStatAsync(ws, ct).ConfigureAwait(false);
        if (stat.IsFailure)
            return Failed($"diff failed: {Describe(stat.Error)}", ws);
        if (stat.Value is not { } changes)
            return Failed("diff failed: the diff stat reported no file list", ws);
        if (changes.Count == 0)
            return Outcome("failed", ReviewHandoff.PublishErrorKey, "nothing to publish");

        // 2. Push. Only this run's own branch, to this run's own remote; the service takes its credentials from the
        // IGitCredentialSource the workspace provider already uses.
        var push = await git.PushAsync(ws, ct).ConfigureAwait(false);
        if (push.IsFailure)
            return Failed($"push failed: {Describe(push.Error)}", ws);

        // 3. Open, or reuse, the pull request.
        var lookup = scope.ServiceProvider.GetRequiredService<IOpenPullRequestLookup>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPullRequestPublisher>();

        var existing = await lookup.FindOpenPullRequestAsync(repository.Remote, ws.Branch, ct).ConfigureAwait(false);
        if (existing.IsFailure)
            return Failed($"pull request lookup failed: {Describe(existing.Error)}", ws);

        var pr = existing.Value;
        if (pr is null)
        {
            var body = PullRequestBody.Render(new PullRequestFacts(
                workIntent,
                changes,
                reviewed.Value,
                run.LastResume is { } resume ? resume.By.DisplayName ?? resume.By.Id : null,
                run.LastResume?.At,
                run.Id,
                run.Process,
                run.ProcessVersion,
                run.Variables.GetValueOrDefault(ReviewHandoff.SummaryKey) as string,
                tested.Value));

            var opened = await publisher.OpenPullRequestAsync(
                ws.Root, ws.Branch, ws.DefaultBranch, PullRequestBody.Title(workIntent), body, ct).ConfigureAwait(false);
            if (opened.IsFailure)
                return Failed($"opening the pull request failed: {Describe(opened.Error)}", ws);

            pr = opened.Value;
        }

        if (pr is not { Url: { Length: > 0 } url })
            return Failed("the pull request host answered with no pull request URL", ws);

        // The URL is stored as the run's pr_url, and the run view shows it as a link, so it must be one. A host that
        // answers with anything else has published somewhere this run cannot point a human to.
        if (!IsWebUrl(url))
            return Failed($"the pull request host answered with '{url}', which is not an absolute http or https URL", ws);

        // No RemoveAsync here (rulings R14 and R19): the sweeper removes the worktree once the run is Succeeded.
        return Outcome("published", ReviewHandoff.PrUrlKey, url);
    }

    /// <summary>
    ///     Why the run's variable bag has no room for a key this action writes, or <see langword="null"/> when it has.
    ///     The action writes exactly one of <see cref="ReviewHandoff.HostActionKeys"/>, so each is checked on its own.
    /// </summary>
    private static string? DescribeFullBag(WorkflowRun run)
    {
        foreach (var key in ReviewHandoff.HostActionKeys)
        {
            var reached = run.Variables.Count + (run.Variables.ContainsKey(key) ? 0 : 1);
            if (reached > ReviewHandoffWorkflowStore.MaxVariableKeys)
            {
                return $"the run's variable bag holds {run.Variables.Count} keys, so writing '{key}' would take it past the " +
                    $"limit of {ReviewHandoffWorkflowStore.MaxVariableKeys}; nothing was committed or pushed";
            }
        }

        return null;
    }

    private static Result<HostActionResult> Outcome(string outcome, string key, string value) =>
        Result<HostActionResult>.Success(new HostActionResult(
            outcome, new Dictionary<string, object?>(StringComparer.Ordinal) { [key] = value }));

    /// <summary>
    ///     What each lens of the approving review visit checked: the review-evidence records at the highest
    ///     <see cref="WorkflowRunRecord.Seq"/>, the last record per lens. A redelivered review node records its lenses
    ///     again at the same seq, and the store lists records in append order within a seq, so the last one is the
    ///     visit's final word. Payloads are read as parsed JSON, since <c>jsonb</c> keeps no text. Each lens's final
    ///     record must be an approval, which is what the body's review label states host code verified.
    /// </summary>
    private static async ValueTask<Result<IReadOnlyList<(string Lens, IReadOnlyList<string> Checked)>>> CheckedAsync(
        Guid runId, IServiceProvider services, CancellationToken ct)
    {
        var records = await services.GetRequiredService<IWorkflowRunRecordStore>()
            .ListAsync(runId, WorkflowRunRecord.ReviewEvidenceKind, ct).ConfigureAwait(false);
        if (records is null)
            return Result<IReadOnlyList<(string, IReadOnlyList<string>)>>.Failure("the run record store answered with no review evidence list");
        if (records.Count == 0)
            return Result<IReadOnlyList<(string, IReadOnlyList<string>)>>.Success([]);

        var approvingSeq = records.Max(r => r.Seq);
        var lenses = new List<(string Lens, bool Approved, IReadOnlyList<string> Checked)>();
        foreach (var record in records.Where(r => r.Seq == approvingSeq))
        {
            var read = ReadEvidence(record);
            if (read.IsFailure)
                return Result<IReadOnlyList<(string, IReadOnlyList<string>)>>.Failure(read.Error);

            var at = lenses.FindIndex(l => string.Equals(l.Lens, read.Value.Lens, StringComparison.Ordinal));
            if (at >= 0)
                lenses[at] = read.Value;
            else
                lenses.Add(read.Value);
        }

        if (lenses.FirstOrDefault(l => !l.Approved) is { Lens: { } rejected })
        {
            return Result<IReadOnlyList<(string, IReadOnlyList<string>)>>.Failure(
                $"the review evidence for lens '{rejected}' at the approving visit is not an approval");
        }

        return Result<IReadOnlyList<(string, IReadOnlyList<string>)>>.Success([.. lenses.Select(l => (l.Lens, l.Checked))]);
    }

    /// <summary>
    ///     The run's last <c>test</c> call as its <see cref="WorkflowRunRecord.TestResultKind"/> record holds it, or null
    ///     when none was recorded. Records list in append order, so the last <c>test</c> record is the last run. A
    ///     <c>build</c> record is not a test result and is not taken for one. A record whose payload cannot be read is
    ///     still reported, as unreadable, so the section never silently claims that no test ran. What it holds is what
    ///     the run's sandbox reported, which ran code from the change; it is not verified.
    /// </summary>
    private static async ValueTask<Result<TestResultFacts?>> LastTestResultAsync(
        Guid runId, IServiceProvider services, CancellationToken ct)
    {
        var records = await services.GetRequiredService<IWorkflowRunRecordStore>()
            .ListAsync(runId, WorkflowRunRecord.TestResultKind, ct).ConfigureAwait(false);
        if (records is null)
            return Result<TestResultFacts?>.Failure("the run record store answered with no test result list");

        TestResultFacts? last = null;
        foreach (var record in records)
        {
            var read = ReadTestResult(record)
                ?? new TestResultFacts(record.Node, "test", SandboxCallRecorder.UnknownExit, SandboxCallRecorder.UnknownSummary);
            if (string.Equals(read.Tool, "test", StringComparison.Ordinal))
                last = read;
        }

        return Result<TestResultFacts?>.Success(last);
    }

    private static TestResultFacts? ReadTestResult(WorkflowRunRecord record)
    {
        try
        {
            using var payload = JsonDocument.Parse(record.PayloadJson);
            var root = payload.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String
                && root.TryGetProperty("exit", out var exit) && exit.ValueKind == JsonValueKind.String
                && root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String)
            {
                return new TestResultFacts(record.Node, tool.GetString()!, exit.GetString()!, summary.GetString()!);
            }
        }
        catch (JsonException)
        {
            // Reported as unreadable by the caller, the same as a payload of the wrong shape.
        }

        return null;
    }

    private static Result<(string Lens, bool Approved, IReadOnlyList<string> Checked)> ReadEvidence(WorkflowRunRecord record)
    {
        try
        {
            using var payload = JsonDocument.Parse(record.PayloadJson);
            var root = payload.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("lens", out var lens) && lens.ValueKind == JsonValueKind.String
                && root.TryGetProperty("verdict", out var verdict) && verdict.ValueKind == JsonValueKind.String
                && root.TryGetProperty("checked", out var examined) && examined.ValueKind == JsonValueKind.Array
                && examined.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
            {
                return Result<(string, bool, IReadOnlyList<string>)>.Success((
                    lens.GetString()!,
                    string.Equals(verdict.GetString(), ReviewEvidence.Approved, StringComparison.Ordinal),
                    [.. examined.EnumerateArray().Select(e => e.GetString()!)]));
            }
        }
        catch (JsonException)
        {
            // Reported below with the record's id, the same as a payload of the wrong shape.
        }

        return Result<(string, bool, IReadOnlyList<string>)>.Failure(
            $"review evidence record {record.Id} has no readable lens, verdict and checked list");
    }

    private static string Describe(AgentError error) =>
        string.IsNullOrWhiteSpace(error.Detail) ? error.Message : $"{error.Message} {error.Detail}";

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal));

    /// <summary>
    ///     A failure after the handoff. A local run's message names the worktree kept for a human. A sandboxed run's
    ///     names no host path: its worktree is in its container, and the publish worktree is Thalos's to rebuild.
    /// </summary>
    private Result<HostActionResult> Failed(string what, RunWorkspace ws) =>
        Result<HostActionResult>.Failure(_sandboxed
            ? $"{what}; the run's changes are kept for a retry"
            : $"{what}; the worktree is kept at {ws.Root}");
}
