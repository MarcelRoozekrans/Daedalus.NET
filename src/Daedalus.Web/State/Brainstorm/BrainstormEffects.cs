using Daedalus.Web.Auth;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Brainstorm;

/// <summary>
///     The Brainstorm page's I/O: read the session, send a message, advance the phase and generate the tasks. Each returns
///     its outcome, done or refused with the API's reason; a session being renewed, or a caller gone, is no outcome,
///     <see langword="null"/>. A caller already gone dispatches nothing, and an answer whose caller went while it was in
///     flight stores nothing. A session being renewed stores nothing and logs nothing. Every other failure is
///     logged as a warning, with its text only: never a message.
/// </summary>
public sealed partial class BrainstormEffects(
    ApiClient api, IDispatcher dispatcher, IStore<BrainstormState> brainstorm, ILogger<BrainstormEffects> logger)
{
    /// <summary>Reads session <paramref name="sessionId"/>: on open, and after a send.</summary>
    public async Task<BrainstormOutcome?> LoadAsync(Guid sessionId, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        // Read and dispatched with no await between, so the next read takes the number after this one's.
        var sequence = brainstorm.Value.Sequence + 1;
        await dispatcher.DispatchAsync(new BrainstormReadStarted(sequence));
        var result = await api.GetBrainstormSessionAsync(sessionId, ct);
        if (ct.IsCancellationRequested || (result.IsFailure && SessionExpiry.IsSigningIn(result.Error)))
        {
            return null;
        }

        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new BrainstormLoaded(sequence, result.Value));
            return BrainstormOutcome.Done;
        }

        LogLoadFailed(logger, result.Error);
        return new BrainstormOutcome(result.Error, 0);
    }

    /// <summary>Sends <paramref name="message"/> to session <paramref name="sessionId"/>; the page reads the session after.</summary>
    public async Task<BrainstormOutcome?> SendAsync(Guid sessionId, string message, CancellationToken ct)
    {
        var result = await api.SendBrainstormMessageAsync(sessionId, new SendBrainstormMessageDto(message), ct);
        if (result.IsSuccess)
        {
            return BrainstormOutcome.Done;
        }

        if (SessionExpiry.IsSigningIn(result.Error) || ct.IsCancellationRequested)
        {
            return null;
        }

        LogSendRefused(logger, result.Error);
        return new BrainstormOutcome(result.Error, 0);
    }

    /// <summary>Advances session <paramref name="sessionId"/> to its next phase; the session the API returns shows.</summary>
    public async Task<BrainstormOutcome?> AdvanceAsync(Guid sessionId, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        // Numbered as a read: the advanced session is newer than any read started before it.
        var sequence = brainstorm.Value.Sequence + 1;
        await dispatcher.DispatchAsync(new BrainstormReadStarted(sequence));
        var result = await api.AdvanceBrainstormPhaseAsync(sessionId, ct);
        if (ct.IsCancellationRequested || (result.IsFailure && SessionExpiry.IsSigningIn(result.Error)))
        {
            return null;
        }

        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new BrainstormLoaded(sequence, result.Value));
            return BrainstormOutcome.Done;
        }

        LogAdvanceRefused(logger, result.Error);
        return new BrainstormOutcome(result.Error, 0);
    }

    /// <summary>Generates the tasks from session <paramref name="sessionId"/>'s plan; the outcome counts them.</summary>
    public async Task<BrainstormOutcome?> GenerateTasksAsync(Guid sessionId, CancellationToken ct)
    {
        var result = await api.GenerateBrainstormTasksAsync(sessionId, ct);
        if (result.IsSuccess)
        {
            return new BrainstormOutcome(null, result.Value.Count);
        }

        if (SessionExpiry.IsSigningIn(result.Error) || ct.IsCancellationRequested)
        {
            return null;
        }

        LogTasksRefused(logger, result.Error);
        return new BrainstormOutcome(result.Error, 0);
    }

    [LoggerMessage(EventId = 3800, Level = LogLevel.Warning, Message = "A brainstorm session could not be loaded: {Error}")]
    private static partial void LogLoadFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3801, Level = LogLevel.Warning, Message = "A brainstorm message was refused: {Reason}")]
    private static partial void LogSendRefused(ILogger logger, string reason);

    [LoggerMessage(EventId = 3802, Level = LogLevel.Warning, Message = "A brainstorm phase could not be advanced: {Reason}")]
    private static partial void LogAdvanceRefused(ILogger logger, string reason);

    [LoggerMessage(EventId = 3803, Level = LogLevel.Warning, Message = "The brainstorm tasks could not be generated: {Reason}")]
    private static partial void LogTasksRefused(ILogger logger, string reason);
}
