using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Endpoints;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Tests.Shared;
using Daedalus.Tests.Unit.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     Phase 2.13: <see cref="WorkflowRunsEndpoints.List"/> with <c>signal</c> or <c>updatedSince</c>, which the store
///     cannot filter on, reads the store page by page and keeps the matches. These pin where it stops and what cursor it
///     returns, against an in-memory store that pages as the real one does: newest first, after a cursor.
/// </summary>
public sealed class WorkflowRunsEndpointsScanTests
{
    private const string Other = "other_signal";
    private const string Gate = "human_approval";

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ITaskQueryService _tasks = Substitute.For<ITaskQueryService>();
    private readonly WorkflowRunGateway _gateway = new(
        Substitute.For<IWorkflowStore>(), Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), Substitute.For<IRunWorkspaceProvider>(),
        new WorkflowConfig(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);

    public WorkflowRunsEndpointsScanTests()
    {
        _tasks.GetRunLinksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TaskRunLink>());
    }

    /// <summary>
    ///     A scan that finds no match reads at most <see cref="RunListLimits.ScanCap"/> summaries and then returns a
    ///     cursor, since matches may remain past it.
    /// </summary>
    [Fact]
    public async Task A_scan_stops_at_the_cap_with_a_cursor()
    {
        var store = new PagedRunQuery(Runs(600, _ => Other));

        var list = await ListAsync(store, take: 20, signal: Gate);

        // Red: string.Equals negated in WorkflowRunsEndpoints.Matches; the other runs then match and fill the page.
        list.Runs.Should().BeEmpty();
        // Red: ScanAsync reading its cap as 600 in place of RunListLimits.ScanCap, in both of its uses; it reads all
        //      600.
        store.Read.Should().Be(RunListLimits.ScanCap);
        // Red: new WorkflowRunPage(kept, null) in place of (kept, after) after ScanAsync's loop.
        list.Next.Should().NotBeNull("runs past the cap may still match");
    }

    /// <summary>
    ///     A scan that reaches the end of the store has no cursor, so a card's count is exact.
    /// </summary>
    [Fact]
    public async Task A_scan_that_reaches_the_end_has_no_cursor()
    {
        var store = new PagedRunQuery(Runs(150, i => i % 50 == 0 ? Gate : Other));

        var list = await ListAsync(store, take: 20, signal: Gate);

        // Red: string.Equals negated in WorkflowRunsEndpoints.Matches; the other runs fill the page of 20.
        list.Runs.Should().HaveCount(3);
        // Red: ScanAsync's end-of-store exit answering new WorkflowRunCursor(page.Runs[^1].CreatedAt, page.Runs[^1].Id)
        //      in place of null.
        list.Next.Should().BeNull();
    }

    /// <summary>
    ///     A scan that fills <c>take</c> returns a cursor at its last match, and that cursor's page goes on with the next
    ///     match, none skipped.
    /// </summary>
    [Fact]
    public async Task A_full_scan_page_continues_from_its_last_match()
    {
        var runs = Runs(10, i => i % 2 == 0 ? Gate : Other);
        var store = new PagedRunQuery(runs);

        var first = await ListAsync(store, take: 2, signal: Gate);
        var second = await ListAsync(store, take: 2, signal: Gate, after: first.Next);

        // Red: string.Equals negated in WorkflowRunsEndpoints.Matches; the odd runs are kept instead.
        first.Runs.Select(r => r.Id).Should().Equal(runs[0].Id, runs[2].Id);
        // Red: the take-filled return of ScanAsync answering the store page's own cursor, page.Next, in place of a
        //      cursor at its last match; the second page then starts over or skips the matches after the last kept one.
        second.Runs.Select(r => r.Id).Should().Equal(runs[4].Id, runs[6].Id);
    }

    /// <summary>
    ///     When <c>take</c> is filled by the store's very last run, there is nothing more, so there is no cursor.
    /// </summary>
    [Fact]
    public async Task A_page_filled_by_the_last_run_has_no_cursor()
    {
        var store = new PagedRunQuery(Runs(4, i => i is 1 or 3 ? Gate : Other));

        var list = await ListAsync(store, take: 2, signal: Gate);

        // Red: kept.Count == filter.Take - 1 in place of kept.Count == filter.Take in ScanAsync; one run comes back.
        list.Runs.Should().HaveCount(2);
        // Red: page.Next is not null in place of page.Next is null in ScanAsync's last test; the cursor is set.
        list.Next.Should().BeNull();
    }

    /// <summary>
    ///     <c>updatedSince</c> keeps a run created long ago and changed recently, wherever it sits in the creation order,
    ///     and leaves out a newer run that has not changed since.
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

        // Red: run.CreatedAt in place of run.UpdatedAt in WorkflowRunsEndpoints.Matches; the old run is dropped.
        // Red: run.UpdatedAt > since in place of >= in WorkflowRunsEndpoints.Matches; the run changed exactly at the
        //      instant is dropped.
        list.Runs.Select(r => r.Id).Should().Equal(runs[30].Id, runs[59].Id);
        // Red: ScanAsync's end-of-store exit answering new WorkflowRunCursor(page.Runs[^1].CreatedAt, page.Runs[^1].Id)
        //      in place of null.
        list.Next.Should().BeNull();
    }

    /// <summary>
    ///     An updatedSince that is not an ISO 8601 timestamp with its zone is refused with a reason: free text, a loose date
    ///     that reads as two different days by culture, a date with no time, and a time with no zone.
    /// </summary>
    [Theory]
    [InlineData("last week")]
    [InlineData("10/08/2026")]
    [InlineData("2026-10-08")]
    [InlineData("2026-10-08T12:00:00")]
    public async Task An_updated_since_that_is_not_an_iso_timestamp_is_400(string updatedSince)
    {
        var result = await ListResultAsync(new PagedRunQuery([]), take: null, updatedSince: updatedSince);

        // Red, per row: for "last week", parsed = DateTimeOffset.MinValue in place of the 400 return of the
        //      updatedSince check in WorkflowRunsEndpoints.List; for "10/08/2026", a format "MM/dd/yyyy" added to
        //      UpdatedSinceFormats; for "2026-10-08", a format "yyyy-MM-dd" added; for "2026-10-08T12:00:00", a format
        //      "yyyy-MM-dd'T'HH:mm:ss" added. That row then answers 200.
        result.Result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    ///     The ISO forms are taken: what Home sends, the UTC round-trip form with seven fraction digits; the offset form a
    ///     DateTimeOffset round-trips to; and whole seconds with Z.
    /// </summary>
    [Theory]
    [InlineData("2026-10-08T12:00:00.0000000Z")]
    [InlineData("2026-10-08T14:00:00.0000000+02:00")]
    [InlineData("2026-10-08T12:00:00Z")]
    public async Task An_iso_updated_since_is_taken(string updatedSince)
    {
        var result = await ListResultAsync(new PagedRunQuery([]), take: null, updatedSince: updatedSince);

        // Red, per row: the Z format dropped from UpdatedSinceFormats in WorkflowRunsEndpoints for the first, the
        //      offset format dropped for the second, and the Z format's fraction made required, fffffff, for the
        //      third; that row then answers 400.
        result.Result.Should().BeOfType<Ok<WorkflowRunList>>();
    }

    /// <summary>
    ///     A store that breaks its contract, returning an empty page with a cursor, ends the scan instead of looping it:
    ///     it is read once and the list says it is complete.
    /// </summary>
    [Fact]
    public async Task An_empty_page_with_a_cursor_ends_the_scan()
    {
        var store = new EmptyPagesWithCursor();

        var list = await ListAsync(store, take: 20, signal: Gate);

        // Red: ScanAsync's end-of-store exit answering page.Next in place of null; the list then names a cursor.
        list.Runs.Should().BeEmpty();
        list.Next.Should().BeNull();
        // Red: the page.Runs.Count == 0 test dropped from ScanAsync's end-of-store exit; the scan asks again and
        //      again, and the store's call limit throws.
        store.Calls.Should().Be(1);
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
        IWorkflowRunQuery store, int take, string? signal = null, string? updatedSince = null, string? after = null)
    {
        var result = await ListResultAsync(store, take, signal, updatedSince, after);
        return result.Result.Should().BeOfType<Ok<WorkflowRunList>>().Which.Value!;
    }

    /// <summary>Calls the handler over <paramref name="store"/> with the given query values.</summary>
    private async Task<Results<Ok<WorkflowRunList>, ApiProblemResult>> ListResultAsync(
        IWorkflowRunQuery store, int? take, string? signal = null, string? updatedSince = null, string? after = null)
    {
        await using var request = WorkflowRunsRequest.For(_gateway, query: store);
        return await request.ListAsync(_tasks, after: after, take: take, signal: signal, updatedSince: updatedSince);
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

    /// <summary>A store that always answers with no runs and a cursor; it fails a test that calls it more than 10 times.</summary>
    private sealed class EmptyPagesWithCursor : IWorkflowRunQuery
    {
        public int Calls { get; private set; }

        public ValueTask<WorkflowRunPage> ListAsync(WorkflowRunFilter filter, CancellationToken ct)
        {
            Calls++;
            if (Calls > 10)
            {
                throw new InvalidOperationException("The scan kept asking an empty store.");
            }

            return ValueTask.FromResult(new WorkflowRunPage([], new WorkflowRunCursor(Start, Guid.Empty)));
        }
    }
}
