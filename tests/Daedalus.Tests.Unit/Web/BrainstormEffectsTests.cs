using System.Net;
using System.Text.Json;
using Daedalus.Application.DTOs;
using Daedalus.Web.State.Brainstorm;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;
using BrainstormPhase = Daedalus.Domain.Entities.BrainstormPhase;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The Brainstorm effects: load, send, advance and generate tasks, each returning its outcome, and what they never
///     store: a session being renewed, or an answer whose page went. A failure is logged with its text, never a message.
/// </summary>
public sealed class BrainstormEffectsTests : IDisposable
{
    private const string Message = "Small shop owners, who need stock counts first.";
    private const string Refusal = "The session has ended.";

    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly string SessionPath = $"/api/brainstorm/sessions/{SessionId}";
    private static readonly string MessagesPath = $"{SessionPath}/messages";
    private static readonly string AdvancePath = $"{SessionPath}/advance";
    private static readonly string TasksPath = $"{SessionPath}/generate-tasks";

    private static readonly BrainstormSessionDto Session = new(
        SessionId, Guid.NewGuid(), BrainstormPhase.ContextGathering,
        [new BrainstormMessageDto(Guid.NewGuid(), "Assistant", "Who is it for?", BrainstormPhase.ContextGathering, DateTime.UnixEpoch)],
        null, null, true, DateTime.UnixEpoch, null);

    private static readonly BrainstormSessionDto Advanced = Session with { Phase = BrainstormPhase.Clarification, PhaseCompleteSignaled = false };

    private static readonly TaskDto Task = new(
        Guid.NewGuid(), "TASK-1", Guid.NewGuid(), "Cart", "A cart", 2, "Implementation", 1, [], [], 2, "p", "DONE", 10, 0, null,
        null, 0, DateTime.UnixEpoch, null, null, null, [], null, null, null, null);

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<BrainstormState> _store;
    private readonly BrainstormEffects _effects;
    private readonly RecordingLogger<BrainstormEffects> _logger = new();

    public BrainstormEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<BrainstormState>>();
        _effects = new BrainstormEffects(_handler.Client(), _provider.GetRequiredService<IDispatcher>(), _store, _logger);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static string RefusalJson => Json(new { error = Refusal });

    private static HttpResponseMessage SigningIn()
    {
        var navigation = new RecordingNavigation();
        throw new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null);
    }

    private static Func<Task<HttpResponseMessage>> Closing(CancellationTokenSource closing) => async () =>
    {
        await closing.CancelAsync();
        throw new OperationCanceledException(closing.Token);
    };

    private async Task LoadedAsync()
    {
        _handler.OnJson(SessionPath, Json(Session));
        await _effects.LoadAsync(SessionId, CancellationToken.None);
    }

    /// <summary>
    ///     A read keeps the session and is done.
    ///     Red: dispatch nothing on a success in LoadAsync; the session stays null.
    ///     Red: return null on a success in LoadAsync; there is no outcome.
    /// </summary>
    [Fact]
    public async Task A_read_keeps_the_session()
    {
        _handler.OnJson(SessionPath, Json(Session));

        var outcome = await _effects.LoadAsync(SessionId, CancellationToken.None);

        _store.Value.Session!.Messages.Should().ContainSingle().Which.Content.Should().Be("Who is it for?");
        outcome.Should().Be(BrainstormOutcome.Done);
    }

    /// <summary>
    ///     A failed read returns its reason, keeps the session held, and is logged as a warning.
    ///     Red: return Done on a failure in LoadAsync; the reason is lost.
    ///     Red: drop the LogLoadFailed call; no warning is written.
    /// </summary>
    [Fact]
    public async Task A_failed_read_says_why_and_keeps_the_session()
    {
        await LoadedAsync();
        _handler.On(SessionPath, () => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var outcome = await _effects.LoadAsync(SessionId, CancellationToken.None);

        outcome!.Value.Refusal.Should().Contain("500");
        _store.Value.Session.Should().BeEquivalentTo(Session);
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    ///     A read whose token cannot be refreshed, or whose page closes in flight, is no outcome, stores nothing and logs
    ///     nothing; a read for a page already gone dispatches nothing.
    ///     Red: drop the SigningInAgain check in LoadAsync; the renewal is a refusal.
    ///     Red: drop the cancelled-token guard at the top of LoadAsync; the sequence moves.
    ///     Red: drop the cancellation check after the read in LoadAsync; the closing page gets "Request was cancelled".
    /// </summary>
    [Fact]
    public async Task A_read_while_signing_in_or_for_a_page_gone_is_no_outcome()
    {
        _handler.On(SessionPath, SigningIn);
        var renewal = await _effects.LoadAsync(SessionId, CancellationToken.None);
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        var sequenceBeforeGone = _store.Value.Sequence;
        var forGone = await _effects.LoadAsync(SessionId, gone.Token);
        var sequenceAfterGone = _store.Value.Sequence;
        using var closing = new CancellationTokenSource();
        _handler.On(SessionPath, Closing(closing));

        var closed = await _effects.LoadAsync(SessionId, closing.Token);

        renewal.Should().BeNull();
        forGone.Should().BeNull();
        sequenceAfterGone.Should().Be(sequenceBeforeGone);
        closed.Should().BeNull();
        _store.Value.Session.Should().BeNull();
        _logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    ///     Two reads close together, such as two sends: the first one's answer lands after the second's and does not
    ///     replace it.
    ///     Red: number every read 1 in LoadAsync; the older session wins.
    /// </summary>
    [Fact]
    public async Task An_older_read_does_not_replace_a_newer_one()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        _handler.On(SessionPath, async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await release.Task;
                return RecordingHandler.Json(HttpStatusCode.OK, Json(Session));
            }

            return RecordingHandler.Json(HttpStatusCode.OK, Json(Advanced));
        });

        var first = _effects.LoadAsync(SessionId, CancellationToken.None);
        await _effects.LoadAsync(SessionId, CancellationToken.None);
        release.SetResult();
        await first;

        _store.Value.Session!.Phase.Should().Be(BrainstormPhase.Clarification);
    }

    /// <summary>
    ///     A send posts the message and is done.
    ///     Red: post an empty message in SendAsync; the posted content is empty.
    ///     Red: return null on a success in SendAsync; there is no outcome.
    /// </summary>
    [Fact]
    public async Task A_send_posts_the_message()
    {
        _handler.OnJson(MessagesPath, Json(Session.Messages[0]));

        var outcome = await _effects.SendAsync(SessionId, Message, CancellationToken.None);

        outcome.Should().Be(BrainstormOutcome.Done);
        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("content").GetString().Should().Be(Message);
    }

    /// <summary>
    ///     A refused send returns the reason, and is logged as a warning with the reason and not the message.
    ///     Red: return Done on a refusal in SendAsync; the reason is lost.
    ///     Red: drop the LogSendRefused call; no warning is written.
    ///     Red: log the message in LogSendRefused; the warning carries it.
    /// </summary>
    [Fact]
    public async Task A_refused_send_says_why()
    {
        _handler.OnJson(MessagesPath, RefusalJson, HttpStatusCode.BadRequest);

        var outcome = await _effects.SendAsync(SessionId, Message, CancellationToken.None);

        outcome!.Value.Refusal.Should().Be(Refusal);
        _logger.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, string Message)>(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(Refusal, StringComparison.Ordinal)
            && !e.Message.Contains(Message, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A send whose token cannot be refreshed, whose page closes in flight, or whose page is already gone, is no
    ///     outcome and logs nothing.
    ///     Red: drop the SigningInAgain check in SendAsync; the renewal is a refusal.
    ///     Red: drop the cancellation check in SendAsync; the closing page, and the page gone, get "Request was cancelled".
    /// </summary>
    [Fact]
    public async Task A_send_while_signing_in_or_for_a_page_gone_is_no_outcome()
    {
        _handler.On(MessagesPath, SigningIn);
        var renewal = await _effects.SendAsync(SessionId, Message, CancellationToken.None);
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        var forGone = await _effects.SendAsync(SessionId, Message, gone.Token);
        using var closing = new CancellationTokenSource();
        _handler.On(MessagesPath, Closing(closing));

        var closed = await _effects.SendAsync(SessionId, Message, closing.Token);

        renewal.Should().BeNull();
        forGone.Should().BeNull();
        closed.Should().BeNull();
        _logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    ///     Advance posts to the session's advance and the session it returns shows, done.
    ///     Red: dispatch nothing on a success in AdvanceAsync; the session stays in its old phase.
    ///     Red: return null on a success in AdvanceAsync; there is no outcome.
    /// </summary>
    [Fact]
    public async Task Advance_shows_the_returned_session()
    {
        await LoadedAsync();
        _handler.OnJson(AdvancePath, Json(Advanced));

        var outcome = await _effects.AdvanceAsync(SessionId, CancellationToken.None);

        _store.Value.Session!.Phase.Should().Be(BrainstormPhase.Clarification);
        outcome.Should().Be(BrainstormOutcome.Done);
        _handler.Requests[^1].Method.Should().Be(HttpMethod.Post);
    }

    /// <summary>
    ///     An advance answers after a read started before it: the read's older session does not replace the advanced one.
    ///     Red: number the advance with the current sequence in AdvanceAsync; the read's older session lands over it.
    /// </summary>
    [Fact]
    public async Task A_read_started_before_an_advance_does_not_replace_it()
    {
        var release = new TaskCompletionSource();
        _handler.On(SessionPath, async () =>
        {
            await release.Task;
            return RecordingHandler.Json(HttpStatusCode.OK, Json(Session));
        });
        _handler.OnJson(AdvancePath, Json(Advanced));

        var read = _effects.LoadAsync(SessionId, CancellationToken.None);
        await _effects.AdvanceAsync(SessionId, CancellationToken.None);
        release.SetResult();
        await read;

        _store.Value.Session!.Phase.Should().Be(BrainstormPhase.Clarification);
    }

    /// <summary>
    ///     A refused advance returns the reason, keeps the session, and is logged as a warning.
    ///     Red: return Done on a refusal in AdvanceAsync; the reason is lost.
    ///     Red: drop the LogAdvanceRefused call; no warning is written.
    /// </summary>
    [Fact]
    public async Task A_refused_advance_says_why_and_keeps_the_session()
    {
        await LoadedAsync();
        _handler.OnJson(AdvancePath, RefusalJson, HttpStatusCode.BadRequest);

        var outcome = await _effects.AdvanceAsync(SessionId, CancellationToken.None);

        outcome!.Value.Refusal.Should().Be(Refusal);
        _store.Value.Session.Should().BeEquivalentTo(Session);
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    ///     An advance whose token cannot be refreshed, or whose page closes in flight, is no outcome, stores nothing and
    ///     logs nothing; one for a page already gone dispatches nothing.
    ///     Red: drop the SigningInAgain check in AdvanceAsync; the renewal is a refusal.
    ///     Red: drop the cancelled-token guard at the top of AdvanceAsync; the sequence moves.
    ///     Red: drop the cancellation check after the request in AdvanceAsync; the closing page gets "Request was cancelled".
    /// </summary>
    [Fact]
    public async Task An_advance_while_signing_in_or_for_a_page_gone_is_no_outcome()
    {
        _handler.On(AdvancePath, SigningIn);
        var renewal = await _effects.AdvanceAsync(SessionId, CancellationToken.None);
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        var sequenceBeforeGone = _store.Value.Sequence;
        var forGone = await _effects.AdvanceAsync(SessionId, gone.Token);
        var sequenceAfterGone = _store.Value.Sequence;
        using var closing = new CancellationTokenSource();
        _handler.On(AdvancePath, Closing(closing));

        var closed = await _effects.AdvanceAsync(SessionId, closing.Token);

        renewal.Should().BeNull();
        forGone.Should().BeNull();
        sequenceAfterGone.Should().Be(sequenceBeforeGone);
        closed.Should().BeNull();
        _store.Value.Session.Should().BeNull();
        _logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    ///     Generating the tasks returns how many were created.
    ///     Red: return Done in GenerateTasksAsync; the count is 0.
    ///     Red: return null on a success in GenerateTasksAsync; there is no outcome.
    /// </summary>
    [Fact]
    public async Task Generating_the_tasks_counts_them()
    {
        _handler.OnJson(TasksPath, Json(new[] { Task, Task with { Id = Guid.NewGuid() } }));

        var outcome = await _effects.GenerateTasksAsync(SessionId, CancellationToken.None);

        outcome.Should().Be(new BrainstormOutcome(null, 2));
    }

    /// <summary>
    ///     A refused task generation returns the reason and is logged as a warning; a renewal, or a page closing in flight,
    ///     is no outcome and logs nothing, and so is one for a page already gone.
    ///     Red: return Done on a refusal in GenerateTasksAsync; the reason is lost.
    ///     Red: drop the LogTasksRefused call; no warning is written.
    ///     Red: drop the SigningInAgain check in GenerateTasksAsync; the renewal is a refusal.
    ///     Red: drop the cancellation check in GenerateTasksAsync; the closing page, and the page gone, get "Request was
    ///     cancelled".
    /// </summary>
    [Fact]
    public async Task A_refused_task_generation_says_why_and_a_renewal_or_page_gone_is_no_outcome()
    {
        _handler.OnJson(TasksPath, RefusalJson, HttpStatusCode.BadRequest);
        var refused = await _effects.GenerateTasksAsync(SessionId, CancellationToken.None);
        var logged = _logger.Entries.Select(e => e.Level).ToArray();
        _logger.Entries.Clear();
        _handler.On(TasksPath, SigningIn);
        var renewal = await _effects.GenerateTasksAsync(SessionId, CancellationToken.None);
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        var forGone = await _effects.GenerateTasksAsync(SessionId, gone.Token);
        using var closing = new CancellationTokenSource();
        _handler.On(TasksPath, Closing(closing));

        var closed = await _effects.GenerateTasksAsync(SessionId, closing.Token);

        refused!.Value.Refusal.Should().Be(Refusal);
        logged.Should().ContainSingle().Which.Should().Be(LogLevel.Warning);
        renewal.Should().BeNull();
        forGone.Should().BeNull();
        closed.Should().BeNull();
        _logger.Entries.Should().BeEmpty();
    }
}
