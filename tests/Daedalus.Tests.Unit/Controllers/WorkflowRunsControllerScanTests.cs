using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Tests.Unit.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Controllers;

/// <summary>
///     Phase 2.13: <see cref="WorkflowRunsController.List"/> with <c>signal</c> or <c>updatedSince</c>, which the store
///     cannot filter on, reads the store page by page and keeps the matches. These pin where it stops and what cursor it
///     returns, against an in-memory store that pages as the real one does: newest first, after a cursor.
/// </summary>
public sealed class WorkflowRunsControllerScanTests
{
    private const string Other = "other_signal";
    private const string Gate = "human_approval";

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ITaskQueryService _tasks = Substitute.For<ITaskQueryService>();
    private readonly WorkflowRunsController _controller;

    public WorkflowRunsControllerScanTests()
    {
        _tasks.GetRunLinksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TaskRunLink>());
        var gateway = new WorkflowRunGateway(
            Substitute.For<IWorkflowStore>(), Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), Substitute.For<IRunWorkspaceProvider>(),
            new WorkflowConfig(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddApiExplorer();
        _controller = new WorkflowRunsController(gateway)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() } },
        };
    }

    /// <summary>
    ///     A scan that finds no match reads at most <see cref="WorkflowRunsController.ScanCap"/> summaries and then returns a
    ///     cursor, since matches may remain past it.
    ///     Red: no cap; it reads all 600 and returns no cursor.
    ///     Red: return no cursor at the cap; the cursor is null.
    /// </summary>
    [Fact]
    public async Task A_scan_stops_at_the_cap_with_a_cursor()
    {
        var store = new PagedRunQuery(Runs(600, _ => Other));

        var list = await ListAsync(store, take: 20, signal: Gate);

        list.Runs.Should().BeEmpty();
        store.Read.Should().Be(WorkflowRunsController.ScanCap);
        list.Next.Should().NotBeNull("runs past the cap may still match");
    }

    /// <summary>
    ///     A scan that reaches the end of the store has no cursor, so a card's count is exact.
    ///     Red: always return the last cursor read; the cursor is set.
    /// </summary>
    [Fact]
    public async Task A_scan_that_reaches_the_end_has_no_cursor()
    {
        var store = new PagedRunQuery(Runs(150, i => i % 50 == 0 ? Gate : Other));

        var list = await ListAsync(store, take: 20, signal: Gate);

        list.Runs.Should().HaveCount(3);
        list.Next.Should().BeNull();
    }

    /// <summary>
    ///     A scan that fills <c>take</c> returns a cursor at its last match, and that cursor's page goes on with the next
    ///     match, none skipped.
    ///     Red: return the store page's own cursor instead; the second page skips the matches after the last kept one.
    /// </summary>
    [Fact]
    public async Task A_full_scan_page_continues_from_its_last_match()
    {
        var runs = Runs(10, i => i % 2 == 0 ? Gate : Other);
        var store = new PagedRunQuery(runs);

        var first = await ListAsync(store, take: 2, signal: Gate);
        var second = await ListAsync(store, take: 2, signal: Gate, after: first.Next);

        first.Runs.Select(r => r.Id).Should().Equal(runs[0].Id, runs[2].Id);
        second.Runs.Select(r => r.Id).Should().Equal(runs[4].Id, runs[6].Id);
    }

    /// <summary>
    ///     When <c>take</c> is filled by the store's very last run, there is nothing more, so there is no cursor.
    ///     Red: always return a cursor once take is filled; the cursor is set.
    /// </summary>
    [Fact]
    public async Task A_page_filled_by_the_last_run_has_no_cursor()
    {
        var store = new PagedRunQuery(Runs(4, i => i is 1 or 3 ? Gate : Other));

        var list = await ListAsync(store, take: 2, signal: Gate);

        list.Runs.Should().HaveCount(2);
        list.Next.Should().BeNull();
    }

    /// <summary>
    ///     <c>updatedSince</c> keeps a run created long ago and changed recently, wherever it sits in the creation order,
    ///     and leaves out a newer run that has not changed since.
    ///     Red: compare CreatedAt; the old run is dropped.
    ///     Red: compare with <c>&gt;</c>; the run changed exactly at the instant is dropped.
    /// </summary>
    [Fact]
    public async Task Updated_since_keeps_an_old_run_that_changed_recently()
    {
        var since = Start.AddDays(10);
        var runs = Runs(60, _ => null);
        runs[59] = runs[59] with { UpdatedAt = since.AddHours(1) };
        runs[30] = runs[30] with { UpdatedAt = since };
        var store = new PagedRunQuery(runs);

        var list = await ListAsync(store, take: 20, updatedSince: since.ToString("O"));

        list.Runs.Select(r => r.Id).Should().Equal(runs[30].Id, runs[59].Id);
        list.Next.Should().BeNull();
    }

    /// <summary>
    ///     A timestamp that does not parse is refused with a reason.
    ///     Red: drop the guard; an unparsed value is ignored and the list is 200.
    /// </summary>
    [Fact]
    public async Task An_updated_since_that_is_not_a_timestamp_is_400()
    {
        var result = await _controller.List(
            null, null, null, null, null, "last week", new PagedRunQuery([]), _tasks, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    ///     <paramref name="count"/> runs, newest first, one minute apart, each awaiting <paramref name="signal"/>'s answer for
    ///     its index, and each last changed when it was created.
    /// </summary>
    private static List<WorkflowRunSummary> Runs(int count, Func<int, string?> signal) =>
        [.. Enumerable.Range(0, count).Select(i => new WorkflowRunSummary
        {
            Id = Guid.NewGuid(),
            Process = "manufacture",
            ProcessVersion = 1,
            Status = WorkflowStatus.Awaiting,
            CurrentNode = "gate",
            AwaitingSignal = signal(i),
            CreatedAt = Start.AddMinutes(count - i),
            UpdatedAt = Start.AddMinutes(count - i),
        })];

    private async Task<WorkflowRunList> ListAsync(
        PagedRunQuery store, int take, string? signal = null, string? updatedSince = null, string? after = null)
    {
        var result = await _controller.List(null, null, after, take, signal, updatedSince, store, _tasks, CancellationToken.None);
        return (WorkflowRunList)result.Should().BeOfType<OkObjectResult>().Which.Value!;
    }

    /// <summary>An in-memory store list: newest first by creation, after a cursor, a page at a time, counting what it hands out.</summary>
    private sealed class PagedRunQuery(IReadOnlyList<WorkflowRunSummary> runs) : IWorkflowRunQuery
    {
        public int Read { get; private set; }

        public ValueTask<WorkflowRunPage> ListAsync(WorkflowRunFilter filter, CancellationToken ct)
        {
            var older = filter.After is { } cursor ? runs.Where(r => r.CreatedAt < cursor.CreatedAt) : runs;
            var rest = older.ToList();
            var page = rest.Take(filter.Take).ToList();
            Read += page.Count;
            var next = rest.Count > page.Count ? new WorkflowRunCursor(page[^1].CreatedAt, page[^1].Id) : null;
            return ValueTask.FromResult(new WorkflowRunPage(page, next));
        }
    }
}
