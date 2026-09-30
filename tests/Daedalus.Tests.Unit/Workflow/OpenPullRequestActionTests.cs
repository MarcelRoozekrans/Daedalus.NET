using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Thalos;
using Thalos.Git;
using Thalos.Git.Workspaces;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Task B13: the paths of <see cref="OpenPullRequestAction"/> the real-git Integration suite cannot reach cheaply:
///     a missing workspace, a collaborator answering null, the cancellation contract, an existing pull request, and the
///     allow-list and evidence checks. Every collaborator is a substitute, so each test states exactly what it answers.
/// </summary>
public sealed class OpenPullRequestActionTests : IDisposable
{
    private const string Remote = "https://github.com/MarcelRoozekrans/daedalus-sandbox.git";
    private static readonly Guid RunId = new(0x1c2d3e4f, 0x5a6b, 0x4c7d, 0x8e, 0x9f, 0x0a, 0x1b, 0x2c, 0x3d, 0x4e, 0x5f);
    private static readonly string[] CanonicalStandingPath = ["docs/AGENT.md"];
    private static readonly ProcessNode PublishNode = new() { Action = OpenPullRequestAction.ActionName, Outcomes = ["published", "failed"] };

    private readonly IRunWorkspaceProvider _workspaces = Substitute.For<IRunWorkspaceProvider>();
    private readonly IRunWorkspaceGit _git = Substitute.For<IRunWorkspaceGit>();
    private readonly IOpenPullRequestLookup _lookup = Substitute.For<IOpenPullRequestLookup>();
    private readonly IPullRequestPublisher _publisher = Substitute.For<IPullRequestPublisher>();
    private readonly IWorkflowRunRecordStore _records = Substitute.For<IWorkflowRunRecordStore>();
    private readonly WorkflowConfig _config = new();
    private readonly ServiceProvider _services;
    private readonly RunWorkspace _ws = new(RunId, "sandbox", Remote, "main", $"manufacture/{RunId}", "C:/data/runs/r1", SolutionPath: null);

    public OpenPullRequestActionTests()
    {
        _config.Repositories.Add(new RepositoryConfig { Name = "sandbox", Remote = Remote });
        _config.CommitAuthor.Name = "Daedalus";
        _config.CommitAuthor.Email = "daedalus@roozekrans.nl";

        var services = new ServiceCollection();
        services.AddScoped(_ => _lookup);
        services.AddScoped(_ => _publisher);
        services.AddSingleton(_records);
        _services = services.BuildServiceProvider();

        _workspaces.FindAsync(RunId, Arg.Any<CancellationToken>()).Returns(new ValueTask<RunWorkspace?>(_ws));
        _git.CommitAsync(_ws, Arg.Any<GitCommitRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<GitCommitResult, AgentError>>(Result<GitCommitResult, AgentError>.Success(new GitCommitResult("abc", Created: true))));
        _git.DiffStatAsync(_ws, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<IReadOnlyList<GitFileChange>, AgentError>>(
                Result<IReadOnlyList<GitFileChange>, AgentError>.Success([new GitFileChange("src/A.cs", 1, 0)])));
        _git.PushAsync(_ws, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Success()));
        _lookup.FindOpenPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<PullRequestResult?, AgentError>>(Result<PullRequestResult?, AgentError>.Success(null)));
        _publisher.OpenPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<PullRequestResult, AgentError>>(Result<PullRequestResult, AgentError>.Success(new PullRequestResult("https://x/pr/7", "7"))));
        _records.ListAsync(RunId, WorkflowRunRecord.ReviewEvidenceKind, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([]));
    }

    public void Dispose() => _services.Dispose();

    private OpenPullRequestAction Action() =>
        new(_workspaces, _git, _services.GetRequiredService<IServiceScopeFactory>(), _config);

    private static WorkflowRun Run(string? workIntent = "Tighten a guard.") => new()
    {
        Id = RunId,
        Process = "manufacture",
        ProcessVersion = 6,
        CurrentNode = "publish",
        CurrentSeq = 9,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
        Manifest = new RunManifest
        {
            Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal),
            Documents = workIntent is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal) { [ManufactureRunStarter.WorkIntentDocument] = workIntent },
        },
    };

    [Fact]
    public async Task A_run_with_no_workspace_is_refused()
    {
        _workspaces.FindAsync(RunId, Arg.Any<CancellationToken>()).Returns(new ValueTask<RunWorkspace?>((RunWorkspace?)null));

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("has no workspace to publish from");
    }

    [Fact]
    public async Task A_run_with_no_pinned_work_intent_is_refused_before_it_commits()
    {
        var result = await Action().RunAsync(Run(workIntent: null), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("work intent").And.Contain(_ws.Root);
        await _git.DidNotReceiveWithAnyArgs().CommitAsync(default!, default!, default);
    }

    [Fact]
    public async Task Both_commits_are_written_as_the_configured_author_and_split_on_the_canonical_standing_instructions_path()
    {
        _config.StandingInstructionsPath = "./docs//AGENT.md";

        await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        await _git.Received(1).CommitAsync(_ws, Arg.Is<GitCommitRequest>(r =>
            r.Author == new GitAuthor("Daedalus", "daedalus@roozekrans.nl") && r.Paths == null
            && r.ExcludePaths != null && r.ExcludePaths.SequenceEqual(CanonicalStandingPath)), Arg.Any<CancellationToken>());
        await _git.Received(1).CommitAsync(_ws, Arg.Is<GitCommitRequest>(r =>
            r.Author == new GitAuthor("Daedalus", "daedalus@roozekrans.nl") && r.ExcludePaths == null
            && r.Paths != null && r.Paths.SequenceEqual(CanonicalStandingPath)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_open_pull_request_for_the_branch_is_reused_and_none_is_opened()
    {
        _lookup.FindOpenPullRequestAsync(Remote, _ws.Branch, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<PullRequestResult?, AgentError>>(Result<PullRequestResult?, AgentError>.Success(new PullRequestResult("https://x/pr/3", "3"))));

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.Value.Outcome.Should().Be("published");
        result.Value.Variables["pr_url"].Should().Be("https://x/pr/3");
        await _publisher.DidNotReceiveWithAnyArgs().OpenPullRequestAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task A_lookup_failure_fails_the_action_and_opens_nothing()
    {
        _lookup.FindOpenPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<PullRequestResult?, AgentError>>(Result<PullRequestResult?, AgentError>.Failure(AgentError.GitOperationFailed("rate limited"))));

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("pull request lookup failed").And.Contain(_ws.Root);
        await _publisher.DidNotReceiveWithAnyArgs().OpenPullRequestAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task A_publisher_answering_success_with_no_pull_request_is_a_clean_failure()
    {
        _publisher.OpenPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<PullRequestResult, AgentError>>(Result<PullRequestResult, AgentError>.Success(null!)));

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no pull request").And.Contain(_ws.Root);
    }

    /// <summary>
    ///     Task B15 fix round 1: the run's <c>pr_url</c> is always a link, so the run view can show it as one. A
    ///     publisher answering with a relative path, another scheme or text fails the action loudly. Red: dropping the
    ///     <c>IsWebUrl</c> check publishes each of these as the run's <c>pr_url</c>.
    /// </summary>
    [Theory]
    [InlineData("pull/7")]
    [InlineData("ftp://x/pr/7")]
    [InlineData("file:///C:/data/pr/7")]
    [InlineData("not a url")]
    public async Task A_publisher_answering_with_a_url_that_is_not_absolute_http_is_a_loud_failure(string answered)
    {
        _publisher.OpenPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<PullRequestResult, AgentError>>(Result<PullRequestResult, AgentError>.Success(new PullRequestResult(answered, "7"))));

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain($"'{answered}'").And.Contain("not an absolute http or https URL").And.Contain(_ws.Root);
    }

    /// <summary>The reused pull request's URL is checked the same way. Red: checking only a newly opened one.</summary>
    [Fact]
    public async Task A_reused_pull_request_whose_url_is_not_absolute_http_is_a_loud_failure()
    {
        _lookup.FindOpenPullRequestAsync(Remote, _ws.Branch, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<PullRequestResult?, AgentError>>(Result<PullRequestResult?, AgentError>.Success(new PullRequestResult("pull/3", "3"))));

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("'pull/3'").And.Contain("not an absolute http or https URL");
    }

    /// <summary>
    ///     A plain http URL, such as a self-hosted forge on a private network, is accepted as it is. Red: accepting
    ///     https only.
    /// </summary>
    [Fact]
    public async Task A_publisher_answering_with_an_http_url_publishes_it()
    {
        _publisher.OpenPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<PullRequestResult, AgentError>>(Result<PullRequestResult, AgentError>.Success(new PullRequestResult("http://forge.internal/pr/7", "7"))));

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Variables["pr_url"].Should().Be("http://forge.internal/pr/7");
    }

    [Fact]
    public async Task A_diff_stat_answering_success_with_no_list_is_a_clean_failure_that_pushes_nothing()
    {
        _git.DiffStatAsync(_ws, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result<IReadOnlyList<GitFileChange>, AgentError>>(Result<IReadOnlyList<GitFileChange>, AgentError>.Success(null!)));

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("diff failed");
        await _git.DidNotReceiveWithAnyArgs().PushAsync(default!, default);
    }

    [Fact]
    public async Task A_repository_no_longer_allow_listed_is_refused_before_it_commits_or_pushes()
    {
        _config.Repositories.Clear();

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no longer allow-listed");
        await _git.DidNotReceiveWithAnyArgs().CommitAsync(default!, default!, default);
        await _git.DidNotReceiveWithAnyArgs().PushAsync(default!, default);
        await _lookup.DidNotReceiveWithAnyArgs().FindOpenPullRequestAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_repository_now_configured_at_another_remote_is_refused_before_it_commits_or_pushes()
    {
        _config.Repositories[0].Remote = "https://github.com/someone-else/daedalus-sandbox.git";

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("different remote");
        await _git.DidNotReceiveWithAnyArgs().CommitAsync(default!, default!, default);
        await _git.DidNotReceiveWithAnyArgs().PushAsync(default!, default);
        await _lookup.DidNotReceiveWithAnyArgs().FindOpenPullRequestAsync(default!, default!, default);
    }

    [Fact]
    public async Task An_unreadable_review_evidence_record_is_refused_before_it_commits_or_pushes()
    {
        var record = WorkflowRunRecord.Create(RunId, 7, "review", WorkflowRunRecord.ReviewEvidenceKind, "workflow:run/review", null,
            """{ "verdict": "approved" }""", DateTime.UtcNow).Value;
        _records.ListAsync(RunId, WorkflowRunRecord.ReviewEvidenceKind, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([record]));

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("review evidence");
        await _git.DidNotReceiveWithAnyArgs().CommitAsync(default!, default!, default);
        await _git.DidNotReceiveWithAnyArgs().PushAsync(default!, default);
        await _publisher.DidNotReceiveWithAnyArgs().OpenPullRequestAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task A_lens_whose_last_record_at_the_approving_visit_is_a_rejection_is_refused_before_it_commits_or_pushes()
    {
        WorkflowRunRecord Evidence(string lens, string verdict) =>
            WorkflowRunRecord.Create(RunId, 7, "review", WorkflowRunRecord.ReviewEvidenceKind, "workflow:run/review", null,
                $$"""{ "lens": "{{lens}}", "verdict": "{{verdict}}", "checked": ["x"], "findings": [] }""", DateTime.UtcNow).Value;
        _records.ListAsync(RunId, WorkflowRunRecord.ReviewEvidenceKind, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([Evidence("correctness", "approved"), Evidence("failure-modes", "rejected")]));

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("lens 'failure-modes'").And.Contain("not an approval");
        await _git.DidNotReceiveWithAnyArgs().CommitAsync(default!, default!, default);
        await _git.DidNotReceiveWithAnyArgs().PushAsync(default!, default);
    }

    [Fact]
    public async Task A_cancelled_dispatch_token_propagates_instead_of_failing_the_run()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _git.PushAsync(_ws, Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = async () => await Action().RunAsync(Run(), PublishNode, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_cancellation_nobody_asked_for_is_a_failed_result_not_an_exception()
    {
        _publisher.OpenPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var result = await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("HttpClient.Timeout").And.Contain(_ws.Root);
    }

    [Fact]
    public async Task No_path_removes_the_workspace()
    {
        await Action().RunAsync(Run(), PublishNode, CancellationToken.None);
        _git.PushAsync(_ws, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Failure(AgentError.GitOperationFailed("gone"))));
        await Action().RunAsync(Run(), PublishNode, CancellationToken.None);

        await _workspaces.DidNotReceiveWithAnyArgs().RemoveAsync(Guid.Empty, default);
    }
}
