using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos;
using Thalos.Git;
using Thalos.Git.Workspaces;
using Thalos.Sandbox;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Task B13: <see cref="OpenPullRequestAction"/> over a real local bare remote, a real
///     <see cref="GitWorktreeWorkspaceProvider"/>, A7's <see cref="GitCliRunWorkspaceGit"/> (ruling R23), the Postgres
///     record store, and a <see cref="FakePullRequestPublisher"/> registered as both the publisher and the open-PR
///     lookup (ruling R28a). The run sits at node <c>publish</c> with a changed <c>src/A.cs</c> and a changed
///     <c>AGENT.md</c> in its worktree. The provider is the action's <see cref="IRunWorkspaceHandoff"/> too, as it is on a
///     local-mode host (phase 2.6, task B5); the sandbox-mode test builds its own handoff worktree.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OpenPullRequestActionTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string OriginalCode = "class A {}\n";
    private const string OriginalStanding = "# standing\n";
    private const string RepositoryName = "sandbox";

    private static readonly ProcessNode PublishNode = new()
    {
        Action = OpenPullRequestAction.ActionName,
        Outcomes = ["published", "failed"],
    };

    private static readonly DateTimeOffset T = new(2026, 9, 28, 10, 30, 0, TimeSpan.Zero);

    private readonly FakePullRequestPublisher _publisher = new();
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("daedalus-b13-data-").FullName;
    private LocalGitRemote _remote = null!;
    private GitWorktreeWorkspaceProvider _workspaces = null!;
    private ServiceProvider _services = null!;
    private OpenPullRequestAction _action = null!;
    private RunWorkspace _ws = null!;
    private WorkflowRun _run = null!;
    private WorkflowConfig _config = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();

        _remote = LocalGitRemote.Create(("src/A.cs", OriginalCode), ("AGENT.md", OriginalStanding));
        var options = new GitWorkspaceOptions { DataRoot = _dataRoot };
        _workspaces = new GitWorktreeWorkspaceProvider(
            options, [], NullLogger<GitWorktreeWorkspaceProvider>.Instance, TimeProvider.System);

        var runId = Guid.NewGuid();
        _ws = (await _workspaces.CreateAsync(
            new RunWorkspaceRequest(runId, RepositoryName, _remote.Url, "main", $"manufacture/{runId}", Solution: null),
            CancellationToken.None)).Value;
        await File.WriteAllTextAsync(Path.Combine(_ws.Root, "src", "A.cs"), "class A { void Guard(object o) => ArgumentNullException.ThrowIfNull(o); }\n");
        await File.WriteAllTextAsync(Path.Combine(_ws.Root, "AGENT.md"), "# standing\n\nAlways add a guard.\n");

        var config = new WorkflowConfig();
        config.Repositories.Add(new RepositoryConfig { Name = RepositoryName, Remote = _remote.Url, DefaultBranch = "main" });
        config.CommitAuthor.Name = "Daedalus";
        config.CommitAuthor.Email = "daedalus@roozekrans.nl";
        _config = config;

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new FixtureDbContextFactory(fixture));
        services.AddSingleton<IWorkflowRunRecordStore, WorkflowRunRecordStore>();
        services.AddSingleton(_publisher);
        services.AddSingleton<IPullRequestPublisher>(_publisher);
        services.AddSingleton<IOpenPullRequestLookup>(_publisher);
        _services = services.BuildServiceProvider();

        _action = new OpenPullRequestAction(
            _workspaces,
            new GitCliRunWorkspaceGit(options, NullLogger<GitCliRunWorkspaceGit>.Instance),
            _services.GetRequiredService<IServiceScopeFactory>(),
            config,
            NullLogger<OpenPullRequestAction>.Instance);

        _run = new WorkflowRun
        {
            Id = runId,
            Process = "manufacture",
            ProcessVersion = 6,
            CurrentNode = "publish",
            CurrentSeq = 9,
            Status = WorkflowStatus.Running,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["publish"] = 1 },
            Variables = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["summary"] = "Added a null check.",
                ["approved_by"] = "forged",
            },
            Manifest = new RunManifest
            {
                Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal),
                Documents = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ManufactureRunStarter.WorkIntentDocument] = "Tighten a guard.",
                },
            },
            LastResume = new RunResume(new RunPrincipal("u-admin", ["admin"]) { DisplayName = "admin" }, T, "human_approval"),
        };

        // Three records at the approving review visit (seq 7), plus an older, rejected visit and a redelivered
        // duplicate of the correctness lens at the same seq. Only the last record per lens at the highest seq counts.
        var store = _services.GetRequiredService<IWorkflowRunRecordStore>();
        await store.AppendAsync(Evidence(3, "security", "rejected", "stale-s"), CancellationToken.None);
        await store.AppendAsync(Evidence(7, "correctness", "approved", "c0-dup"), CancellationToken.None);
        await store.AppendAsync(Evidence(7, "correctness", "approved", "c1"), CancellationToken.None);
        await store.AppendAsync(Evidence(7, "failure-modes", "approved", "f1"), CancellationToken.None);
        await store.AppendAsync(Evidence(7, "maintainability", "approved", "m1"), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _workspaces.RemoveAsync(_run.Id, CancellationToken.None);
        LocalGitRemote.DeleteReadOnly(_dataRoot);
        _remote.Dispose();
    }

    [Fact]
    public async Task A_full_pass_makes_a_code_commit_then_an_AGENT_md_commit_pushes_and_opens_one_PR()
    {
        var result = await _action.RunAsync(_run, PublishNode, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Outcome.Should().Be("published");
        result.Value.Variables["pr_url"].Should().Be(FakePullRequestPublisher.Url);
        var log = _remote.Log($"manufacture/{_run.Id}", count: 2);   // newest first
        log[0].Files.Should().Equal("AGENT.md");
        log[1].Files.Should().Contain("src/A.cs").And.NotContain("AGENT.md");
        log[1].Message.Should().Be($"feat: Tighten a guard.\n\nManufactured by run {_run.Id}.");
        LocalGitRemote.Git(_remote.Url, $"log -2 --format=%an|%ae manufacture/{_run.Id}")
            .Split('\n').Should().Equal("Daedalus|daedalus@roozekrans.nl", "Daedalus|daedalus@roozekrans.nl");
        _publisher.OpenCount.Should().Be(1);
        _publisher.LastTitle.Should().Be("manufacture: Tighten a guard.");
        _publisher.LastTargetBranch.Should().Be("main");
        Directory.Exists(_ws.Root).Should().BeTrue("the action never removes the worktree; the sweeper does, once the run is Succeeded");
    }

    [Fact]
    public async Task The_opened_PR_body_reads_the_last_evidence_per_lens_at_the_approving_visit_and_the_recorded_approver()
    {
        await _action.RunAsync(_run, PublishNode, CancellationToken.None);

        var body = _publisher.LastBody!;
        body.Should().Contain("  - `c1`").And.Contain("  - `f1`").And.Contain("  - `m1`");
        body.Should().NotContain("stale-s", "the rejected visit at an older seq is not the approving review");
        body.Should().NotContain("c0-dup", "a redelivered lens record is superseded by the last one at the same seq");
        body.Should().Contain("Approved at the gate by admin").And.NotContain("forged");
        body.Should().Contain("src/A.cs").And.Contain("AGENT.md");
    }

    [Fact]
    public async Task A_redelivery_still_produces_one_PR()
    {
        await _action.RunAsync(_run, PublishNode, CancellationToken.None);
        var pushedHead = _remote.HeadOf($"manufacture/{_run.Id}");

        // As if the first delivery died after opening the PR, before the run advanced. The worktree is still there,
        // because the action never removes it, so the redelivery runs against the same one.
        (await _action.RunAsync(_run, PublishNode, CancellationToken.None)).Value.Outcome.Should().Be("published");

        _publisher.OpenCount.Should().Be(1);
        _remote.HeadOf($"manufacture/{_run.Id}").Should().Be(pushedHead, "the redelivery found nothing to commit and pushed nothing new");
    }

    [Fact]
    public async Task An_empty_diff_is_the_failed_outcome()
    {
        RevertAllChanges();

        var result = await _action.RunAsync(_run, PublishNode, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Outcome.Should().Be("failed");
        result.Value.Variables["publish_error"].Should().Be("nothing to publish");
        _publisher.OpenCount.Should().Be(0);
    }

    [Fact]
    public async Task A_push_failure_keeps_the_worktree_and_names_it()
    {
        _remote.Delete();   // push target gone

        var result = await _action.RunAsync(_run, PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().StartWith("push failed: ").And.Contain(_ws.Root);
        Directory.Exists(_ws.Root).Should().BeTrue();
        _publisher.OpenCount.Should().Be(0);
    }

    /// <summary>
    ///     A failed git step's detail, raw stderr that can hold host paths, is logged, not stored in the run's error,
    ///     which the run view shows over HTTP; the message stays. Red, per assertion: describe the error with its detail
    ///     again, and the first fails on git's <c>fatal:</c> line; drop the log, and the second fails.
    /// </summary>
    [Fact]
    public async Task A_failed_push_keeps_git_stderr_out_of_the_run_error_and_logs_it()
    {
        var logger = new CapturingLogger();
        var action = new OpenPullRequestAction(
            _workspaces,
            new GitCliRunWorkspaceGit(new GitWorkspaceOptions { DataRoot = _dataRoot }, NullLogger<GitCliRunWorkspaceGit>.Instance),
            _services.GetRequiredService<IServiceScopeFactory>(),
            _config,
            logger);
        _remote.Delete();

        var result = await action.RunAsync(_run, PublishNode, CancellationToken.None);

        result.Error.Should().StartWith("push failed: ").And.NotContain("fatal:");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Message.Contains("fatal:", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Phase 2.6, task B5, carry 1: in sandbox mode the handoff worktree holds the run's patch as Thalos's
    ///     <see cref="GitPatchApplier"/> staged it, and that index is what the publish-side protected-path check passed
    ///     (S5). Publish's code commit must be exactly that index. Here the real applier stages a patch, into a real publish
    ///     worktree with <c>core.fileMode=false</c>, that adds a file the patch's own <c>.gitignore</c> change ignores and
    ///     makes <c>run.sh</c> executable; the host then writes the standing-instructions file, unstaged, as the writer
    ///     would; and the action, over A7's real git, commits and pushes. The handoff is a substitute that answers that
    ///     worktree, since the sandbox provider's own needs a container.
    /// </summary>
    /// <remarks>
    ///     The setup guard, that the applier staged both changes: Red: drop the <c>add -f out.gen</c> or the
    ///     <c>update-index --chmod=+x</c> from the patch. The code commit is the applied index: Red: commit without
    ///     <see cref="GitCommitRequest.CommitStagedIndex"/> in sandbox mode; the reset and <c>add -A</c> then drop
    ///     <c>out.gen</c> and the mode change, so the tree differs. The standing-instructions file is its own, later commit:
    ///     Red: skip the path-scoped commit; the newest commit is then the code commit.
    /// </remarks>
    [Fact]
    public async Task In_sandbox_mode_the_code_commit_is_exactly_the_index_the_patch_applier_checked()
    {
        using var remote = LocalGitRemote.Create(
            ("src/A.cs", OriginalCode), ("AGENT.md", OriginalStanding), ("run.sh", "#!/bin/sh\n"), (".gitignore", "*.log\n"));
        var publish = new GitWorkspaceOptions { DataRoot = Path.Combine(_dataRoot, "publish") };
        var publishWorktrees = new GitWorktreeWorkspaceProvider(
            publish, [], NullLogger<GitWorktreeWorkspaceProvider>.Instance, TimeProvider.System);
        var ws = (await publishWorktrees.CreateAsync(
            new RunWorkspaceRequest(_run.Id, RepositoryName, remote.Url, "main", $"manufacture/{_run.Id}", Solution: null),
            CancellationToken.None)).Value;
        LocalGitRemote.GitArgs(ws.Root, "config", "core.fileMode", "false");
        var patch = BuildPatch(remote, ws.BaseCommit!, dir =>
        {
            File.AppendAllText(Path.Combine(dir, ".gitignore"), "*.gen\n");
            File.WriteAllText(Path.Combine(dir, "out.gen"), "generated\n");
            LocalGitRemote.GitArgs(dir, "add", "-f", "out.gen");
            LocalGitRemote.GitArgs(dir, "update-index", "--chmod=+x", "run.sh");
            File.WriteAllText(Path.Combine(dir, "src", "A.cs"), "class A { void Guard(object o) => ArgumentNullException.ThrowIfNull(o); }\n");
            if (!OperatingSystem.IsWindows())
            {
                // The scratch clone's add -A re-reads the mode from disk where core.fileMode is true.
                var script = Path.Combine(dir, "run.sh");
                File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute);
            }
        });

        var applied = await new GitPatchApplier(publish, NullLogger<GitPatchApplier>.Instance).ApplyAsync(
            ws, patch, new ProtectedPathSet([.. SandboxOptions.DefaultProtectedPaths, "AGENT.md"]), new PatchApplyLimits(), CancellationToken.None);
        applied.IsSuccess.Should().BeTrue(applied.IsFailure ? applied.Error.Message + " " + applied.Error.Detail : "");
        var appliedTree = LocalGitRemote.GitArgs(ws.Root, "write-tree");
        LocalGitRemote.GitArgs(ws.Root, "ls-tree", appliedTree, "out.gen", "run.sh")
            .Should().Contain("out.gen").And.Contain("100755", "the applier staged both changes the commit must keep");
        await File.WriteAllTextAsync(Path.Combine(ws.Root, "AGENT.md"), "# standing\n\nAlways add a guard.\n");

        _config.Sandbox.Enabled = true;
        _config.Repositories[0].Remote = remote.Url;
        var handoff = Substitute.For<IRunWorkspaceHandoff>();
        handoff.CheckoutForPublishAsync(_run.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<RunWorkspace, AgentError>>(Result<RunWorkspace, AgentError>.Success(ws)));
        var action = new OpenPullRequestAction(
            handoff,
            new GitCliRunWorkspaceGit(publish, NullLogger<GitCliRunWorkspaceGit>.Instance),
            _services.GetRequiredService<IServiceScopeFactory>(),
            _config,
            NullLogger<OpenPullRequestAction>.Instance);

        var result = await action.RunAsync(_run, PublishNode, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Outcome.Should().Be("published");
        var log = remote.Log($"manufacture/{_run.Id}", count: 2);   // newest first
        log[0].Files.Should().Equal("AGENT.md");
        LocalGitRemote.GitArgs(remote.Url, "rev-parse", log[1].Sha + "^{tree}").Should().Be(appliedTree);
        await publishWorktrees.RemoveAsync(_run.Id, CancellationToken.None);
    }

    /// <summary>
    ///     A patch from <paramref name="baseCommit"/> to <paramref name="edit"/>'s change, made in a scratch clone of
    ///     <paramref name="remote"/> with line endings left exactly as committed, the way a sandbox exports one.
    /// </summary>
    private string BuildPatch(LocalGitRemote remote, string baseCommit, Action<string> edit)
    {
        var scratch = Path.Combine(_dataRoot, "scratch-" + Guid.NewGuid().ToString("N"));
        LocalGitRemote.GitArgs(_dataRoot, "-c", "core.autocrlf=false", "clone", "-q", "--branch", "main", remote.Url, scratch);
        LocalGitRemote.GitArgs(scratch, "config", "core.autocrlf", "false");
        LocalGitRemote.GitArgs(scratch, "checkout", "-q", "--detach", baseCommit);
        edit(scratch);
        LocalGitRemote.GitArgs(scratch, "add", "-A");
        var patch = Path.Combine(_dataRoot, "patch-" + Guid.NewGuid().ToString("N") + ".patch");
        LocalGitRemote.GitArgs(scratch, "diff", "--cached", "--binary", "--full-index", "--output=" + patch, baseCommit);
        return patch;
    }

    private void RevertAllChanges()
    {
        File.WriteAllText(Path.Combine(_ws.Root, "src", "A.cs"), OriginalCode);
        File.WriteAllText(Path.Combine(_ws.Root, "AGENT.md"), OriginalStanding);
    }

    private WorkflowRunRecord Evidence(long seq, string lens, string verdict, string item) =>
        WorkflowRunRecord.Create(
            _run.Id, seq, "review", WorkflowRunRecord.ReviewEvidenceKind, "workflow:run/review", "u-admin",
            $$"""{ "lens": "{{lens}}", "verdict": "{{verdict}}", "checked": ["{{item}}"], "findings": [] }""",
            DateTime.UtcNow).Value;

    /// <summary>Records each entry's level, formatted message and exception.</summary>
    private sealed class CapturingLogger : ILogger<OpenPullRequestAction>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    /// <summary>The store disposes every context it creates, so the factory needs no tracking.</summary>
    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
