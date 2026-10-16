using Daedalus.Web.Auth;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Repositories;

/// <summary>
///     The Git Repositories page's I/O, through <see cref="RepositoriesApiClient"/>, which keeps the page's own failure
///     text. An expected failure is an action, never an exception, and the effect shows nothing: the page words each
///     outcome through <see cref="RepositoriesView"/>. A session being renewed and a request whose caller went away store
///     nothing and log nothing. A failure that is stored is logged as a warning, with its text only.
/// </summary>
public sealed partial class RepositoriesEffects(
    RepositoriesApiClient api, IDispatcher dispatcher, IStore<RepositoriesState> repositories,
    ILogger<RepositoriesEffects> logger)
{
    /// <summary>
    ///     Reads the repositories: the list, or the Forbidden state for a 403. A failure is
    ///     <see cref="RepositoriesState.Error"/>; a <see cref="SessionExpiry.SigningInAgain"/> failure, or one after
    ///     <paramref name="ct"/> was cancelled, shows nothing.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        // A caller already gone, such as a page left before a late write's reload, dispatches nothing: a start would
        // clear what a page opened since shows and move the sequence past its answer.
        if (ct.IsCancellationRequested)
        {
            return;
        }

        // Read and dispatched with no await between: the dispatch reduces at once, on the one browser thread, so the
        // next read takes the number after this one's.
        var sequence = repositories.Value.Sequence + 1;
        await dispatcher.DispatchAsync(new RepositoriesLoadStarted(sequence));
        var result = await api.GetAllAsync(ct);
        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new RepositoriesLoaded(sequence, result.Value));
        }
        else if (!SessionExpiry.IsSigningIn(result.Error) && !ct.IsCancellationRequested)
        {
            LogLoadFailed(logger, result.Error);
            await dispatcher.DispatchAsync(new RepositoriesLoadFailed(sequence, result.Error));
        }
    }

    /// <summary>Adds a repository; returns the outcome; see <see cref="SaveAsync"/>.</summary>
    public Task<RepositorySave?> CreateAsync(CreateRepositoryConfigurationDto repository, CancellationToken ct) =>
        SaveAsync(RepositoryWrite.Created, token => api.CreateAsync(repository, token), ct);

    /// <summary>Updates repository <paramref name="id"/>; returns the outcome; see <see cref="SaveAsync"/>.</summary>
    public Task<RepositorySave?> UpdateAsync(Guid id, UpdateRepositoryConfigurationDto repository, CancellationToken ct) =>
        SaveAsync(RepositoryWrite.Updated, token => api.UpdateAsync(id, repository, token), ct);

    /// <summary>Deletes repository <paramref name="id"/>; returns the outcome; see <see cref="SaveAsync"/>.</summary>
    public Task<RepositorySave?> DeleteAsync(Guid id, CancellationToken ct) =>
        SaveAsync(RepositoryWrite.Deleted, token => api.DeleteAsync(id, token), ct);

    /// <summary>
    ///     Tests the connection to repository <paramref name="id"/>; returns the outcome, passed or refused like a write;
    ///     see <see cref="SaveAsync"/>.
    /// </summary>
    public Task<RepositorySave?> TestConnectionAsync(Guid id, CancellationToken ct) =>
        SaveAsync(RepositoryWrite.Tested, token => api.TestConnectionAsync(id, token), ct);

    /// <summary>
    ///     Runs a write and returns its outcome: saved, or refused with the client's text. A
    ///     <see cref="SessionExpiry.SigningInAgain"/> failure, or one after <paramref name="ct"/> was cancelled, is no
    ///     outcome, <see langword="null"/>: the session is being renewed, or the caller is gone. The outcome is not
    ///     stored: the caller shows what its own write returned, so it never shows another write's outcome.
    /// </summary>
    private async Task<RepositorySave?> SaveAsync(RepositoryWrite write, Func<CancellationToken, Task<Result>> send, CancellationToken ct)
    {
        var result = await send(ct);
        if (result.IsSuccess)
        {
            return new RepositorySave(write, null);
        }

        if (SessionExpiry.IsSigningIn(result.Error) || ct.IsCancellationRequested)
        {
            return null;
        }

        LogWriteRefused(logger, write, result.Error);
        return new RepositorySave(write, result.Error);
    }

    [LoggerMessage(EventId = 3400, Level = LogLevel.Warning, Message = "The repositories could not be loaded: {Error}")]
    private static partial void LogLoadFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3401, Level = LogLevel.Warning, Message = "A repository write ({Write}) was refused: {Reason}")]
    private static partial void LogWriteRefused(ILogger logger, RepositoryWrite write, string reason);
}
