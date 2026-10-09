using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.7: <see cref="FileReviewFindingsAction"/>. GitHub and the record store are substitutes, so every test
///     states exactly what each one answers, and the records the action appends are replayed into later listings. A
///     retry then sees what the first attempt wrote, as the real append-only store does.
/// </summary>
public sealed class FileReviewFindingsActionTests : IDisposable
{
    private const string PrUrl = "https://github.com/MarcelRoozekrans/daedalus-sandbox/pull/7";
    private static readonly Guid RunId = Guid.Parse("9b1c2d3e-4f50-4617-8a9b-0c1d2e3f4a5b");
    private static readonly DateTimeOffset ResumedAt = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
    private static readonly RepoRef Repo = RepoRef.Parse("MarcelRoozekrans/daedalus-sandbox").Value;
    private static readonly ProcessNode Node = new() { Action = FileReviewFindingsAction.ActionName, Outcomes = ["filed", "none"] };

    private readonly IGitHubReader _reader = Substitute.For<IGitHubReader>();
    private readonly IGitHubWriter _writer = Substitute.For<IGitHubWriter>();
    private readonly IWorkflowRunRecordStore _records = Substitute.For<IWorkflowRunRecordStore>();
    private readonly List<WorkflowRunRecord> _stored = [];
    private readonly WorkflowConfig _config = new();
    private readonly ServiceProvider _services;

    public FileReviewFindingsActionTests()
    {
        _config.Repositories.Add(new RepositoryConfig { Name = "sandbox", Remote = "https://github.com/MarcelRoozekrans/daedalus-sandbox.git" });

        var services = new ServiceCollection();
        services.AddScoped(_ => _reader);
        services.AddScoped(_ => _writer);
        services.AddSingleton(_records);
        _services = services.BuildServiceProvider();

        _records.ListAsync(RunId, null, Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<IReadOnlyList<WorkflowRunRecord>>([.. _stored]));
        _records.When(r => r.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>())).Do(c => _stored.Add(c.Arg<WorkflowRunRecord>()));

        _reader.GetPullRequestHeadShaAsync(SameRepo(), 7, Arg.Any<CancellationToken>()).Returns(Result<string>.Success("abc123"));
        _reader.ListIssuesUpdatedSinceAsync(SameRepo(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<IssueText>>.Success([]));
        _reader.ListIssueCommentsSinceAsync(SameRepo(), Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<IssueCommentText>>.Success([]));
        _writer.CreateIssueAsync(SameRepo(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<CreatedIssue>.Success(new CreatedIssue(21, IssueUrl(21))));
        _writer.CommentAsync(SameRepo(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<string>.Success("ok"));
    }

    public void Dispose() => _services.Dispose();

    /// <summary>RepoRef has no value equality and the action parses its own instance from the pull request link, so match by name.</summary>
    private static RepoRef SameRepo() => Arg.Is<RepoRef>(r => string.Equals(r.ToString(), Repo.ToString(), StringComparison.Ordinal));

    private static Uri IssueUrl(int number) => new($"https://github.com/MarcelRoozekrans/daedalus-sandbox/issues/{number}");

    private FileReviewFindingsAction Action() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), _config, TimeProvider.System, NullLogger<FileReviewFindingsAction>.Instance);

    private static WorkflowRun Run(string? prUrl = PrUrl) => new()
    {
        Id = RunId,
        Process = "manufacture",
        ProcessVersion = 9,
        CurrentNode = "file-findings",
        CurrentSeq = 14,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
        Variables = prUrl is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal) { [ReviewHandoff.PrUrlKey] = prUrl },
        LastResume = new RunResume(new RunPrincipal("u-admin", ["admin"]) { DisplayName = "admin" }, ResumedAt, "human_approval"),
    };

    private void Deferred(params string[] entries) =>
        _stored.Add(WorkflowRunRecord.Create(RunId, 9, "review", WorkflowRunRecord.ReviewEvidenceKind, "p", null,
            $$"""{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{{string.Join(",", entries)}}] }""",
            DateTime.UtcNow).Value);

    private static string Entry(string title, int? existing = null) =>
        $$"""{"file":"src/A.cs","line":3,"title":"{{title}}","scenario":"s","reason":"different-area"{{(existing is { } n ? $",\"existingIssue\":{n}" : "")}}}""";

    private static WorkflowRunRecord DropRecord(string attempt, params string[] ids) =>
        WorkflowRunRecord.Create(RunId, 12, "gate", WorkflowRunRecord.FindingsDroppedKind, "u-admin", null,
            FindingRecords.DroppedPayload(ids, "admin", attempt), DateTime.UtcNow).Value;

    private void Dropped(params string[] ids) => _stored.Add(DropRecord(Guid.NewGuid().ToString("N"), ids));

    /// <summary>Red: call GitHub before the empty check; the DidNotReceive assertions fail.</summary>
    [Fact]
    public async Task Nothing_deferred_is_none_and_touches_no_github()
    {
        Deferred();

        var result = await Action().RunAsync(Run(), Node, CancellationToken.None);

        result.Value.Outcome.Should().Be(FileReviewFindingsAction.NoneOutcome);
        await _reader.DidNotReceiveWithAnyArgs().GetPullRequestHeadShaAsync(default!, default, default);
        await _writer.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default);
    }

    /// <summary>Red: ignore the drop record; an issue is then created.</summary>
    [Fact]
    public async Task Everything_dropped_is_none()
    {
        Deferred(Entry("a"));
        Dropped("correctness-1");

        (await Action().RunAsync(Run(), Node, CancellationToken.None)).Value.Outcome.Should().Be(FileReviewFindingsAction.NoneOutcome);
        await _writer.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default);
    }

    /// <summary>
    ///     A drop whose resume failed is voided, and the finding it named must still be filed. Red: read the drop records
    ///     without pairing the void; the finding is then treated as dropped and no issue is created.
    /// </summary>
    [Fact]
    public async Task A_voided_drop_is_ignored_and_its_finding_is_filed()
    {
        Deferred(Entry("keep"));
        _stored.Add(DropRecord("a", "correctness-1"));
        _stored.Add(WorkflowRunRecord.Create(RunId, 12, "gate", WorkflowRunRecord.FindingsDropVoidedKind, "u-admin", null,
            FindingRecords.DropVoidedPayload("a"), DateTime.UtcNow).Value);

        var result = await Action().RunAsync(Run(), Node, CancellationToken.None);

        result.Value.Outcome.Should().Be(FileReviewFindingsAction.FiledOutcome);
        await _writer.Received(1).CreateIssueAsync(SameRepo(), "keep", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Red: skip the drop filter, and two issues are created. Red: skip the summary, and the CommentAsync on #7
    ///     assertion fails.
    /// </summary>
    [Fact]
    public async Task Undropped_findings_are_filed_and_the_pull_request_gets_one_summary()
    {
        Deferred(Entry("keep"), Entry("drop"));
        Dropped("correctness-2");

        var result = await Action().RunAsync(Run(), Node, CancellationToken.None);

        result.Value.Outcome.Should().Be(FileReviewFindingsAction.FiledOutcome);
        await _writer.Received(1).CreateIssueAsync(SameRepo(), "keep", Arg.Is<string>(b => b.Contains(DeferredFindingText.Marker(RunId, "correctness-1"))), Arg.Any<CancellationToken>());
        await _writer.Received(1).CommentAsync(SameRepo(), 7, Arg.Is<string>(b => b.Contains("Dropped at the gate by admin")), Arg.Any<CancellationToken>());
        _stored.Count(r => r.Kind == WorkflowRunRecord.FindingFiledKind).Should().Be(2, "one finding and the summary");
    }

    /// <summary>D2: an open issue the reviewer named is commented on. Red: always create; the CommentAsync on #8 assertion fails.</summary>
    [Fact]
    public async Task An_open_existing_issue_is_commented_on_instead_of_filing_a_new_one()
    {
        Deferred(Entry("dup", existing: 8));
        _reader.GetIssueAsync(SameRepo(), 8, Arg.Any<CancellationToken>())
            .Returns(Result<IssueDetail?>.Success(new IssueDetail(8, "x", "open", "", [], IssueUrl(8), false)));

        await Action().RunAsync(Run(), Node, CancellationToken.None);

        await _writer.Received(1).CommentAsync(SameRepo(), 8, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _writer.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default);
    }

    /// <summary>A closed, missing or pull-request pointer never swallows a finding. Red: comment regardless; no issue is created.</summary>
    [Theory]
    [InlineData("closed", false)]
    [InlineData("open", true)]
    public async Task A_pointer_that_cannot_be_used_falls_back_to_a_new_issue_that_says_why(string state, bool isPr)
    {
        Deferred(Entry("dup", existing: 8));
        _reader.GetIssueAsync(SameRepo(), 8, Arg.Any<CancellationToken>())
            .Returns(Result<IssueDetail?>.Success(new IssueDetail(8, "x", state, "", [], IssueUrl(8), isPr)));

        await Action().RunAsync(Run(), Node, CancellationToken.None);

        await _writer.Received(1).CreateIssueAsync(SameRepo(), "dup", Arg.Is<string>(b => b.Contains("#8")), Arg.Any<CancellationToken>());
        await _writer.DidNotReceive().CommentAsync(SameRepo(), 8, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The admin retry after a partial success: correctness-1 is recorded, and correctness-2 was created on GitHub
    ///     but its record never landed. Red: skip the recorded check, and #21 is created again. Red: skip the marker scan,
    ///     and correctness-2 is created again.
    /// </summary>
    [Fact]
    public async Task A_retry_duplicates_nothing()
    {
        Deferred(Entry("one"), Entry("two"));
        _stored.Add(WorkflowRunRecord.Create(RunId, 14, "file-findings", WorkflowRunRecord.FindingFiledKind, "u-admin", null,
            FindingRecords.FiledPayload(new FiledFinding("correctness-1", FindingRecords.CreatedMode, 21, IssueUrl(21))), DateTime.UtcNow).Value);
        _reader.ListIssuesUpdatedSinceAsync(SameRepo(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<IssueText>>.Success([new IssueText(22, "x " + DeferredFindingText.Marker(RunId, "correctness-2"), IssueUrl(22), false)]));

        var result = await Action().RunAsync(Run(), Node, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Outcome.Should().Be(FileReviewFindingsAction.FiledOutcome);
        await _writer.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default);
        FindingRecords.ReadFiled(_stored).Value["correctness-2"].Issue.Should().Be(22);
    }

    /// <summary>A3: a GitHub error fails the run so the admin retry can re-run this node. Red: return outcome "none" on error.</summary>
    [Fact]
    public async Task A_github_error_is_a_failed_result_naming_the_finding_and_no_host_path()
    {
        Deferred(Entry("one"));
        _writer.CreateIssueAsync(SameRepo(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<CreatedIssue>.Failure("GitHub request forbidden. GitHub said: Resource not accessible"));

        var result = await Action().RunAsync(Run(), Node, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("correctness-1").And.Contain("Resource not accessible");
        result.Error.Should().NotContainAny(":\\", "/data/", "/tmp/");
    }

    /// <summary>A4. Red: skip the allow-list check; an issue is then created in the removed repository.</summary>
    [Fact]
    public async Task A_repository_no_longer_allow_listed_is_refused()
    {
        Deferred(Entry("one"));
        _config.Repositories.Clear();

        (await Action().RunAsync(Run(), Node, CancellationToken.None)).IsFailure.Should().BeTrue();
        await _writer.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default);
    }

    /// <summary>Red: skip the refusal, and each case fails. Red: accept a non-pull path, and the issues link fails. Red: drop the host check, and the gitlab pull link fails.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("https://gitlab.com/o/r/-/merge_requests/7")]
    [InlineData("https://gitlab.com/MarcelRoozekrans/daedalus-sandbox/pull/7")]
    [InlineData("https://github.com/MarcelRoozekrans/daedalus-sandbox/issues/7")]
    public async Task A_missing_or_foreign_pull_request_link_is_refused(string? prUrl)
    {
        Deferred(Entry("one"));

        (await Action().RunAsync(Run(prUrl), Node, CancellationToken.None)).IsFailure.Should().BeTrue();
    }
}
