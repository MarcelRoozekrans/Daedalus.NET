using Daedalus.Agents.Workflow;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>Phase 2.8, amendment A2: a task's status is read from its run through an Application port.</summary>
public sealed class WorkflowRunStatusReaderTests
{
    private readonly IWorkflowStore _store = Substitute.For<IWorkflowStore>();

    private WorkflowRun Given(WorkflowStatus status, string? prUrl = null)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (prUrl is not null)
        {
            variables[ReviewHandoff.PrUrlKey] = prUrl;
        }

        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            Process = "manufacture",
            ProcessVersion = 9,
            CurrentNode = "implement",
            CurrentSeq = 1,
            Status = status,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal),
            Variables = variables,
        };
        _store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        return run;
    }

    /// <summary>Red per row: map that engine status to another state in the reader's switch.</summary>
    [Theory]
    [InlineData(WorkflowStatus.Running, WorkflowRunState.Running)]
    [InlineData(WorkflowStatus.Awaiting, WorkflowRunState.Awaiting)]
    [InlineData(WorkflowStatus.Succeeded, WorkflowRunState.Succeeded)]
    [InlineData(WorkflowStatus.Failed, WorkflowRunState.Failed)]
    [InlineData(WorkflowStatus.Cancelled, WorkflowRunState.Cancelled)]
    public async Task Each_engine_status_maps_to_its_own_state(WorkflowStatus status, WorkflowRunState expected)
    {
        var run = Given(status);

        (await new WorkflowRunStatusReader(_store).ReadAsync(run.Id, CancellationToken.None)).State.Should().Be(expected);
    }

    /// <summary>Red: return <c>Running</c> for a missing run; the state is not <c>Unknown</c>.</summary>
    [Fact]
    public async Task A_run_the_store_does_not_have_is_unknown()
    {
        _store.FindAsync(default, default).ReturnsForAnyArgs(new ValueTask<WorkflowRun?>((WorkflowRun?)null));

        (await new WorkflowRunStatusReader(_store).ReadAsync(Guid.NewGuid(), CancellationToken.None)).Should().Be(WorkflowRunStatus.Unknown);
    }

    /// <summary>
    ///     The pull request link is the run's <c>pr_url</c> when it is an http or https URL.
    ///     Red: read another variable; the link is null. Red: accept any text; the second case is not null. Red: drop the scheme check; the third case, an absolute
    ///     URI that is not http or https, is not null.
    /// </summary>
    [Fact]
    public async Task The_pull_request_link_is_read_from_pr_url_only_when_it_is_a_web_url()
    {
        var published = Given(WorkflowStatus.Succeeded, "https://github.com/o/r/pull/7");
        var garbled = Given(WorkflowStatus.Succeeded, "not a url");
        var otherScheme = Given(WorkflowStatus.Succeeded, "ftp://example.com/pull/7");
        var reader = new WorkflowRunStatusReader(_store);

        (await reader.ReadAsync(published.Id, CancellationToken.None)).PullRequestUrl.Should().Be(new Uri("https://github.com/o/r/pull/7"));
        (await reader.ReadAsync(garbled.Id, CancellationToken.None)).PullRequestUrl.Should().BeNull();
        (await reader.ReadAsync(otherScheme.Id, CancellationToken.None)).PullRequestUrl.Should().BeNull();
    }

    /// <summary>Red: make <c>IsLive</c> true for <c>Succeeded</c>, or false for <c>Awaiting</c>.</summary>
    [Theory]
    [InlineData(WorkflowRunState.Running, true)]
    [InlineData(WorkflowRunState.Awaiting, true)]
    [InlineData(WorkflowRunState.Succeeded, false)]
    [InlineData(WorkflowRunState.Failed, false)]
    [InlineData(WorkflowRunState.Cancelled, false)]
    [InlineData(WorkflowRunState.Unknown, false)]
    public void Only_a_running_or_awaiting_run_is_live(WorkflowRunState state, bool live) =>
        new WorkflowRunStatus(state, null).IsLive.Should().Be(live);

    /// <summary>Red: return <c>Running</c> from the disabled reader.</summary>
    [Fact]
    public async Task The_disabled_reader_answers_unknown()
    {
        (await new DisabledWorkflowRunStatusReader().ReadAsync(Guid.NewGuid(), CancellationToken.None)).Should().Be(WorkflowRunStatus.Unknown);
    }
}
