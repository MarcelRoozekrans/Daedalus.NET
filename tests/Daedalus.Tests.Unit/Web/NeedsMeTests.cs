using Daedalus.Web.Auth;
using Daedalus.Web.Services;

namespace Daedalus.Tests.Unit.Web;

/// <summary>Phase 2.13, #351: what Home's four cards take from the run list, and how long a run has waited.</summary>
public sealed class NeedsMeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     The awaiting card asks the server for runs parked at the human gate, so a page is never taken up by runs waiting
    ///     on another signal.
    ///     Red: AwaitingQuery without Signal; the path has no signal.
    ///     Red: ToPath leaves Signal out; the path has no signal.
    /// </summary>
    [Fact]
    public void The_awaiting_card_asks_the_server_for_the_human_gate() =>
        NeedsMe.AwaitingQuery.ToPath().Should().Be("/api/workflow-runs?take=20&status=Awaiting&signal=human_approval");

    /// <summary>
    ///     The failed card asks the server for failed runs that last changed in the last 7 days, as a UTC timestamp.
    ///     Red: a 30-day window; the timestamp is 2026-09-15.
    ///     Red: ToPath leaves UpdatedSince out; the path has no updatedSince.
    ///     Red: ToPath writes the local time with its offset; the path is not the escaped UTC Z form.
    /// </summary>
    [Fact]
    public void The_failed_card_asks_the_server_for_the_last_7_days() =>
        NeedsMe.FailedQuery(new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.FromHours(2))).ToPath().Should()
            .Be("/api/workflow-runs?take=20&status=Failed&updatedSince=2026-10-08T12%3A00%3A00.0000000Z");

    /// <summary>
    ///     The pull requests card reads manufacture runs only, so scheduled and replay runs do not take its page.
    ///     Red: PullRequestQuery with no process; the path has no process.
    /// </summary>
    [Fact]
    public void The_pull_requests_card_reads_manufacture_runs() =>
        NeedsMe.PullRequestQuery.ToPath().Should().Be("/api/workflow-runs?take=20&process=manufacture");

    /// <summary>
    ///     With no pull request found, the card says there are none only when it read the whole list; otherwise it says
    ///     only the latest 20 runs had none.
    ///     Red: always "No pull requests yet."; the incomplete case is wrong.
    ///     Red: the two texts swapped; the complete case is wrong.
    /// </summary>
    [Fact]
    public void No_pull_requests_says_how_much_was_read()
    {
        NeedsMe.NoPullRequests(more: false).Should().Be("No pull requests yet.");
        NeedsMe.NoPullRequests(more: true).Should().Be("No pull requests in the latest 20 runs.");
    }

    /// <summary>
    ///     The pull requests card lists the first 5 runs, in the list's newest-first order, that have a pull request.
    ///     Red: no PrUrl filter; a run without a pull request is listed.
    ///     Red: take 6; six rows are listed.
    /// </summary>
    [Fact]
    public void Recent_pull_requests_are_the_5_newest_runs_with_one()
    {
        var runs = Enumerable.Range(1, 7)
            .Select(n => RunViews.Item(Guid.NewGuid(), "Succeeded") with { PrUrl = new Uri($"https://github.com/acme/widgets/pull/{n}") })
            .ToList();
        runs.Insert(1, RunViews.Item(Guid.NewGuid(), "Running"));

        NeedsMe.RecentPullRequests(runs).Should().Equal(runs[0], runs[2], runs[3], runs[4], runs[5]);
    }

    /// <summary>
    ///     A row names the run's task, or, for a run with no task, the process and the run's short id.
    ///     Red: a run with no task named by its id alone; the process is missing.
    /// </summary>
    [Fact]
    public void A_run_is_named_by_its_task_or_its_process_and_short_id()
    {
        var id = Guid.NewGuid();
        NeedsMe.Name(RunViews.Item(id) with { TaskTitle = "Ship the widget" }).Should().Be("Ship the widget");
        NeedsMe.Name(RunViews.Item(id)).Should().Be($"manufacture run {id.ToString()[..8]}");
    }

    /// <summary>
    ///     A card shows a failure's reason, but not the failure of a request whose session ended: sign-in is under way.
    ///     Red: return the error as is; the signing-in failure is shown.
    ///     Red: return null for every error; the reason is lost.
    /// </summary>
    [Fact]
    public void A_cards_error_is_its_reason_unless_signing_in()
    {
        NeedsMe.ShownError("The run store is down.").Should().Be("The run store is down.");
        NeedsMe.ShownError(SessionExpiry.SigningInAgain).Should().BeNull();
    }

    /// <summary>
    ///     How long a run has waited reads in the largest whole unit: under a minute, minutes, hours up to two days, then
    ///     days. A clock behind the server reads as under a minute, never a negative wait.
    ///     Red: hours up to one day; the 30-hour row reads "1 d".
    ///     Red: round instead of truncating; the 90-second row reads "2 min".
    ///     Red: no floor at zero; the future row reads "-1 min".
    /// </summary>
    [Theory]
    [InlineData(-60, "less than a minute")]
    [InlineData(30, "less than a minute")]
    [InlineData(90, "1 min")]
    [InlineData(59 * 60, "59 min")]
    [InlineData(60 * 60, "1 h")]
    [InlineData(30 * 3600, "30 h")]
    [InlineData(48 * 3600, "2 d")]
    public void A_wait_reads_in_its_largest_whole_unit(int seconds, string text) =>
        LocalTimes.Elapsed(Now.AddSeconds(-seconds), Now).Should().Be(text);

    /// <summary>
    ///     A pull request link reads #N for a URL ending in its number, else "Pull request".
    ///     Red: return the whole URL; the first row is not "#42".
    /// </summary>
    [Theory]
    [InlineData("https://github.com/acme/widgets/pull/42", "#42")]
    [InlineData("https://example.com/review/abc", "Pull request")]
    public void A_pull_request_link_reads_its_number(string address, string text) =>
        TaskStatusLabels.PullRequestText(new Uri(address)).Should().Be(text);
}
