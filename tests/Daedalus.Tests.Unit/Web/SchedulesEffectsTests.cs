using System.Net;
using System.Text.Json;
using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Web.State.Schedules;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The Schedules effects: the overview, a schedule's run history, a resend's outcome, and what they never store: a
///     session being renewed, or a read whose page went.
/// </summary>
public sealed class SchedulesEffectsTests : IDisposable
{
    private const string OverviewPath = "/api/schedules";

    private static readonly Guid ScheduleId = Guid.NewGuid();
    private static readonly Guid ExecutionId = Guid.NewGuid();
    private static readonly string HistoryPath = $"/api/schedules/{ScheduleId}/runs?take=20";
    private static readonly string ResendPath = $"/api/schedules/{ScheduleId}/runs/{ExecutionId}/resend";

    private static readonly RunDiagnosis Run = new(
        RunVerdict.Undelivered, ScheduleId, "Weekly digest", DateTime.UnixEpoch, 4, null, null, 1, "refused", 3,
        DateTime.UnixEpoch, DateTime.UnixEpoch, ExecutionId, true, null, null, null);

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<SchedulesState> _store;
    private readonly SchedulesEffects _effects;
    private readonly RecordingLogger<SchedulesEffects> _logger = new();

    public SchedulesEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<SchedulesState>>();
        _effects = new SchedulesEffects(_handler.Client(), _provider.GetRequiredService<IDispatcher>(), _store, _logger);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    private static string Json(params string[] names) =>
        JsonSerializer.Serialize(names.Select(n => Run with { ScheduleName = n }), JsonSerializerOptions.Web);

    private static HttpResponseMessage SigningIn()
    {
        var navigation = new RecordingNavigation();
        throw new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null);
    }

    private static HttpResponseMessage ServerError() => new(HttpStatusCode.InternalServerError);

    /// <summary>
    ///     The overview keeps every schedule the API lists.
    ///     Red: dispatch nothing on a success in LoadAsync; the overview stays null.
    /// </summary>
    [Fact]
    public async Task The_overview_keeps_every_schedule()
    {
        _handler.OnJson(OverviewPath, Json("Morning", "Weekly"));

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Overview.Should().HaveCount(2);
    }

    /// <summary>
    ///     A failed overview is the error, with the status, and is logged as a warning.
    ///     Red: dispatch nothing on a failure in LoadAsync; the error stays null.
    ///     Red: drop the LogOverviewFailed call; no warning is written.
    /// </summary>
    [Fact]
    public async Task A_failed_overview_is_the_error_and_logs_a_warning()
    {
        _handler.On(OverviewPath, ServerError);

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().Contain("500");
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    ///     An overview whose token cannot be refreshed stores no error and keeps the spinner, and is not logged.
    ///     Red: dispatch SchedulesLoadFailed for SigningInAgain too; the error is the signing-in text.
    ///     Red: log the failure before the SigningInAgain check in LoadAsync; the renewal writes a warning.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_stores_no_error()
    {
        _handler.On(OverviewPath, SigningIn);

        await _effects.LoadAsync(CancellationToken.None);

        _store.Value.Error.Should().BeNull();
        SchedulesView.Overview(_store.Value).Should().Be(SchedulesShow.Loading);
        _logger.Entries.Should().BeEmpty("a session being renewed is not a failure");
    }

    /// <summary>
    ///     A read for a page already gone sends nothing and dispatches nothing; one whose page closes while it is in flight
    ///     stores nothing and logs nothing.
    ///     Red: drop the cancelled-token guard at the top of LoadAsync; the sequence moves.
    ///     Red: drop the cancelled-token guard after the read in LoadAsync; the cancellation is the error.
    /// </summary>
    [Fact]
    public async Task A_read_for_a_page_gone_changes_nothing()
    {
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        await _effects.LoadAsync(gone.Token);
        var sequenceAfterGone = _store.Value.Sequence;
        using var closing = new CancellationTokenSource();
        _handler.On(OverviewPath, async () =>
        {
            await closing.CancelAsync();
            throw new OperationCanceledException(closing.Token);
        });

        await _effects.LoadAsync(closing.Token);

        sequenceAfterGone.Should().Be(0);
        _store.Value.Error.Should().BeNull();
        _logger.Entries.Should().BeEmpty("a page closing is not a failure");
    }

    /// <summary>
    ///     Two overview reads close together, such as Retry clicked twice: the first one's answer lands after the second's
    ///     and does not replace it.
    ///     Red: number every read 1 in LoadAsync; the older overview wins.
    /// </summary>
    [Fact]
    public async Task An_older_overview_does_not_replace_a_newer_one()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        _handler.On(OverviewPath, async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await release.Task;
                return RecordingHandler.Json(HttpStatusCode.OK, Json("old"));
            }

            return RecordingHandler.Json(HttpStatusCode.OK, Json("new"));
        });

        var first = _effects.LoadAsync(CancellationToken.None);
        await _effects.LoadAsync(CancellationToken.None);
        release.SetResult();
        await first;

        _store.Value.Overview.Should().ContainSingle().Which.ScheduleName.Should().Be("new");
    }

    /// <summary>
    ///     Expanding a schedule reads its last 20 runs once: expanding it again reads nothing.
    ///     Red: dispatch nothing on a success in LoadHistoryAsync; the history stays loading.
    ///     Red: drop the held check in EnsureHistoryAsync; the history is read twice.
    ///     Red: read the history with take 10; the path asks for 10 runs.
    /// </summary>
    [Fact]
    public async Task A_schedules_history_is_read_once()
    {
        _handler.OnJson(HistoryPath, Json("Weekly"));

        await _effects.EnsureHistoryAsync(ScheduleId, CancellationToken.None);
        await _effects.EnsureHistoryAsync(ScheduleId, CancellationToken.None);

        _store.Value.Histories[ScheduleId].Runs.Should().ContainSingle();
        _handler.Paths.Should().Equal(HistoryPath);
    }

    /// <summary>
    ///     A failed history is its error and is logged; Retry reads it again.
    ///     Red: dispatch nothing on a failure in LoadHistoryAsync; the history stays loading.
    ///     Red: drop the LogHistoryFailed call; no warning is written.
    ///     Red: make RetryHistoryAsync call EnsureHistoryAsync; the Retry reads nothing.
    /// </summary>
    [Fact]
    public async Task A_failed_history_is_its_error_and_retry_reads_it_again()
    {
        _handler.On(HistoryPath, ServerError);
        await _effects.EnsureHistoryAsync(ScheduleId, CancellationToken.None);
        var failed = _store.Value.Histories[ScheduleId];
        _handler.OnJson(HistoryPath, Json("Weekly"));

        await _effects.RetryHistoryAsync(ScheduleId, CancellationToken.None);

        failed.Error.Should().Contain("500");
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        _store.Value.Histories[ScheduleId].Runs.Should().ContainSingle();
    }

    /// <summary>
    ///     A history whose token cannot be refreshed stores no error and is not logged; one for a page gone sends nothing.
    ///     Red: dispatch ScheduleHistoryFailed for SigningInAgain too; the history's error is the signing-in text.
    ///     Red: log the failure before the SigningInAgain check in LoadHistoryAsync; the renewal writes a warning.
    ///     Red: drop the cancelled-token guard at the top of LoadHistoryAsync; the gone page's schedule shows loading.
    ///     Red: drop the cancelled-token guard after the read in LoadHistoryAsync; the cancellation is the history's error.
    /// </summary>
    [Fact]
    public async Task A_history_stores_no_renewal_and_nothing_for_a_page_gone()
    {
        var goneId = Guid.NewGuid();
        var closingId = Guid.NewGuid();
        _handler.On(HistoryPath, SigningIn);
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        using var closing = new CancellationTokenSource();
        _handler.On($"/api/schedules/{closingId}/runs?take=20", async () =>
        {
            await closing.CancelAsync();
            throw new OperationCanceledException(closing.Token);
        });

        await _effects.EnsureHistoryAsync(ScheduleId, CancellationToken.None);
        await _effects.EnsureHistoryAsync(goneId, gone.Token);
        await _effects.EnsureHistoryAsync(closingId, closing.Token);

        _store.Value.Histories[ScheduleId].Error.Should().BeNull();
        _store.Value.Histories.Should().NotContainKey(goneId);
        _store.Value.Histories[closingId].Error.Should().BeNull();
        _logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    ///     A resend the API accepts posts that run's execution and returns Requeued; it stores nothing.
    ///     Red: post run.ScheduleId as the execution in ResendAsync; the path names the wrong execution.
    ///     Red: return null on a success in ResendAsync; there is no outcome.
    /// </summary>
    [Fact]
    public async Task An_accepted_resend_is_requeued()
    {
        _handler.On(ResendPath, () => new HttpResponseMessage(HttpStatusCode.OK));

        var outcome = await _effects.ResendAsync(Run, CancellationToken.None);

        outcome.Should().Be(ScheduleResend.Requeued);
        _handler.Requests.Should().ContainSingle().Which.Should().Match<RecordedRequest>(r =>
            r.Method == HttpMethod.Post && r.PathAndQuery == ResendPath);
    }

    /// <summary>
    ///     A resend the API refuses returns the refusal's reason and is logged.
    ///     Red: return Requeued on a refusal in ResendAsync; the page would claim a requeue.
    ///     Red: drop the LogResendRefused call; no warning is written.
    /// </summary>
    [Fact]
    public async Task A_refused_resend_says_why()
    {
        _handler.OnJson(ResendPath, """{"error":"The message is no longer in the outbox."}""", HttpStatusCode.BadRequest);

        var outcome = await _effects.ResendAsync(Run, CancellationToken.None);

        outcome.Should().NotBeNull();
        outcome!.Value.Succeeded.Should().BeFalse();
        outcome.Value.Refusal.Should().Contain("no longer in the outbox");
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    ///     A resend whose token cannot be refreshed, or whose caller went, is no outcome, and is not logged.
    ///     Red: drop the SigningInAgain check in ResendAsync; the outcome is the signing-in text.
    ///     Red: drop the cancellation check in ResendAsync; the outcome is "Request was cancelled".
    /// </summary>
    [Fact]
    public async Task A_renewal_or_a_caller_gone_is_no_outcome()
    {
        _handler.On(ResendPath, SigningIn);
        var renewal = await _effects.ResendAsync(Run, CancellationToken.None);
        using var closing = new CancellationTokenSource();
        _handler.On(ResendPath, async () =>
        {
            await closing.CancelAsync();
            throw new OperationCanceledException(closing.Token);
        });

        var gone = await _effects.ResendAsync(Run, closing.Token);

        renewal.Should().BeNull();
        gone.Should().BeNull();
        _logger.Entries.Should().BeEmpty();
    }
}
