using System.Net;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.State.Runs;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The runs effects: what they fetch on each trigger, and that failures become actions, not exceptions.</summary>
public sealed class RunsEffectsTests : IDisposable
{
    private const string CountPath = "/api/workflow-runs?take=100&status=Awaiting";

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<RunsState> _store;
    private readonly RunsEffects _effects;

    public RunsEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<RunsState>>();
        _effects = new RunsEffects(_handler.Client(), _provider.GetRequiredService<IDispatcher>(), _store);
        _handler.OnJson(CountPath, RunViews.Json(new WorkflowRunList([], null)));
    }

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    /// <summary>
    ///     A change for the selected run re-fetches it; a change for another run does not.
    ///     Red: re-fetch on every change. Red: never re-fetch the selected run.
    ///     Red: keep the first loaded run in the RunLoaded reducer.
    /// </summary>
    [Fact]
    public async Task A_change_re_fetches_only_the_selected_run()
    {
        var selected = Guid.NewGuid();
        var other = Guid.NewGuid();
        _handler.OnJson($"/api/workflow-runs/{selected}", RunViews.Json(RunViews.Sample(selected)));
        await _effects.LoadRunAsync(selected, CancellationToken.None);
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(other, "Running", "build", null), CancellationToken.None);

        _handler.Paths.Should().NotContain(p => p.StartsWith("/api/workflow-runs/", StringComparison.Ordinal));

        _handler.OnJson($"/api/workflow-runs/{selected}", RunViews.Json(RunViews.Sample(selected, "Succeeded", null)));
        await _effects.OnRunChangedAsync(new WorkflowRunChanged(selected, "Succeeded", "done", null), CancellationToken.None);

        _handler.Paths.Should().Contain($"/api/workflow-runs/{selected}");
        _store.Value.Selected!.Status.Should().Be("Succeeded");
    }

    /// <summary>
    ///     A change for a run on the shown page, or any change while the first page is shown, re-fetches the list; a
    ///     change for a run not on a later page does not.
    ///     Red: re-fetch the list only for listed runs. Red: always re-fetch the list.
    ///     Red: re-fetch the list only while the first page is shown.
    /// </summary>
    [Fact]
    public async Task A_change_re_fetches_the_list_when_it_could_show_it()
    {
        var listed = Guid.NewGuid();
        var page = RunViews.Json(new WorkflowRunList([RunViews.Item(listed)], "next"));
        var first = RunListQuery.First;
        var later = RunListQuery.First with { After = "c1" };
        _handler.OnJson(first.ToPath(), page).OnJson(later.ToPath(), page);

        await _effects.LoadListAsync(first, CancellationToken.None);
        _handler.Requests.Clear();
        await _effects.OnRunChangedAsync(new WorkflowRunChanged(Guid.NewGuid(), "Running", "build", null), CancellationToken.None);
        _handler.Paths.Should().Contain(first.ToPath(), "a new run appears on the first page");

        await _effects.LoadListAsync(later, CancellationToken.None);
        _handler.Requests.Clear();
        await _effects.OnRunChangedAsync(new WorkflowRunChanged(Guid.NewGuid(), "Running", "build", null), CancellationToken.None);
        _handler.Paths.Should().NotContain(later.ToPath(), "the run is not on this later page");

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(listed, "Failed", "build", null), CancellationToken.None);
        _handler.Paths.Should().Contain(later.ToPath(), "the run is on this page");
    }

    /// <summary>
    ///     Reconnect re-fetches the selected run, the list and the awaiting-approval count.
    ///     Red: drop any one of the three from RefreshAllAsync.
    /// </summary>
    [Fact]
    public async Task Reconnect_re_fetches_everything_on_screen()
    {
        var id = Guid.NewGuid();
        var later = RunListQuery.First with { After = "c1" };
        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)))
            .OnJson(later.ToPath(), RunViews.Json(new WorkflowRunList([], null)));
        await _effects.LoadListAsync(later, CancellationToken.None);
        await _effects.LoadRunAsync(id, CancellationToken.None);
        _handler.Requests.Clear();

        await _effects.RefreshAllAsync(CancellationToken.None);

        _handler.Paths.Should().BeEquivalentTo([$"/api/workflow-runs/{id}", later.ToPath(), CountPath]);
    }

    /// <summary>
    ///     A failed approve records the server's reason on the draft and re-fetches the run; it never throws.
    ///     Red: let the failure escape. Red: skip the re-fetch after a failure.
    ///     Red: return Result.Success after a refusal. Red: drop the GateSubmitFailed dispatch.
    ///     Red: dispatch GateSubmitSucceeded instead of GateSubmitFailed.
    ///     Red: dispatch GateSubmitSucceeded before GateSubmitFailed.
    /// </summary>
    [Fact]
    public async Task A_refused_approve_keeps_the_draft_and_says_why()
    {
        var id = Guid.NewGuid();
        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)))
            .OnJson($"/api/workflow-runs/{id}/resume", """{"status":409,"detail":"The run is no longer at the gate."}""",
                HttpStatusCode.Conflict);
        var dispatcher = _provider.GetRequiredService<IDispatcher>();
        await _effects.LoadRunAsync(id, CancellationToken.None);
        await dispatcher.DispatchAsync(new GateFindingToggled("mechanism-1", Dropped: true));
        _handler.Requests.Clear();

        var result = await _effects.ApproveAsync(CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _store.Value.Gate.Submitting.Should().BeFalse();
        _store.Value.Gate.Error.Should().Be("The run is no longer at the gate.");
        _store.Value.Gate.Dropped.Should().Equal("mechanism-1");
        _handler.Paths.Should().Equal($"/api/workflow-runs/{id}/resume", $"/api/workflow-runs/{id}");
    }

    /// <summary>
    ///     Once the run page is left, a change for that run fetches no run, and its gate draft is gone.
    ///     Red: keep SelectedId on RunClosed. Red: keep the Gate on RunClosed.
    /// </summary>
    [Fact]
    public async Task After_the_run_is_closed_a_change_fetches_no_run()
    {
        var id = Guid.NewGuid();
        _handler.OnJson($"/api/workflow-runs/{id}", RunViews.Json(RunViews.Sample(id)));
        var dispatcher = _provider.GetRequiredService<IDispatcher>();
        await _effects.LoadRunAsync(id, CancellationToken.None);
        await dispatcher.DispatchAsync(new GateFindingToggled("mechanism-1", Dropped: true));
        await dispatcher.DispatchAsync(new RunClosed());
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(id, "Succeeded", "done", null), CancellationToken.None);

        _handler.Paths.Should().NotContain($"/api/workflow-runs/{id}");
        _store.Value.Gate.Dropped.Should().BeEmpty();
    }

    /// <summary>
    ///     Once the list page is left, a change fetches no list, even for a run the list showed on the first page.
    ///     Red: keep List on RunListClosed.
    /// </summary>
    [Fact]
    public async Task After_the_list_is_closed_a_change_fetches_no_list()
    {
        var listed = Guid.NewGuid();
        _handler.OnJson(RunListQuery.First.ToPath(), RunViews.Json(new WorkflowRunList([RunViews.Item(listed)], null)));
        await _effects.LoadListAsync(RunListQuery.First, CancellationToken.None);
        await _provider.GetRequiredService<IDispatcher>().DispatchAsync(new RunListClosed());
        _handler.Requests.Clear();

        await _effects.OnRunChangedAsync(new WorkflowRunChanged(listed, "Failed", "build", null), CancellationToken.None);

        _handler.Paths.Should().Equal(CountPath);
    }

    /// <summary>
    ///     The awaiting-approval count counts only human_approval rows, and says there are more when Next is set.
    ///     Red: count every Awaiting row. Red: ignore Next.
    /// </summary>
    [Fact]
    public async Task The_count_counts_only_approval_gates()
    {
        _handler.OnJson(CountPath, RunViews.Json(new WorkflowRunList(
            [
                RunViews.Item(Guid.NewGuid(), "Awaiting", "human_approval"),
                RunViews.Item(Guid.NewGuid(), "Awaiting", "other_signal"),
                RunViews.Item(Guid.NewGuid(), "Awaiting", "human_approval"),
            ],
            "more")));

        await _effects.LoadAwaitingApprovalAsync(CancellationToken.None);

        _store.Value.AwaitingApproval.Should().Be(2);
        _store.Value.AwaitingApprovalMore.Should().BeTrue();
    }
}
