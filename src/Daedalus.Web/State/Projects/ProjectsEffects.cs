using Daedalus.Web.Auth;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Projects;

/// <summary>
///     The projects feature's I/O: the one load the Tasks page, the task create dialog and Home's New task share. Each
///     reader calls <see cref="LoadAsync"/> when it opens; see <see cref="ProjectsState"/> for why nothing clears the
///     list. An expected failure is an action, never an exception. A session being renewed and a load whose reader went
///     away store no error.
/// </summary>
public sealed class ProjectsEffects(ApiClient api, IDispatcher dispatcher, IStore<ProjectsState> projects)
{
    /// <summary>Every reader reads one page of this many projects, the size the Tasks page has always read.</summary>
    public const int Page = 200;

    /// <summary>
    ///     The last load's sequence number. Static, so that every effects instance over a store numbers its loads in one
    ///     order; only the order matters, never the value.
    /// </summary>
    private static long s_lastSequence;

    /// <summary>
    ///     Fetches the projects. A failure is <see cref="ProjectsState.Error"/>; a <see cref="SessionExpiry.SigningInAgain"/>
    ///     failure, or one after <paramref name="ct"/> was cancelled, only ends the load.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var sequence = Interlocked.Increment(ref s_lastSequence);
        await dispatcher.DispatchAsync(new ProjectsLoadStarted(sequence));
        var result = await api.GetProjectsAsync(1, Page, ct);
        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new ProjectsLoaded(sequence, result.Value.Items));
        }
        else if (SessionExpiry.IsSigningIn(result.Error) || ct.IsCancellationRequested)
        {
            await dispatcher.DispatchAsync(new ProjectsLoadAbandoned(sequence));
        }
        else
        {
            await dispatcher.DispatchAsync(new ProjectsLoadFailed(sequence, result.Error));
        }
    }

    /// <summary>
    ///     Re-fetches the projects after a reconnect, when a load was ever made, because a project may have been added or
    ///     renamed meanwhile. Projects no reader asked for are left alone.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (projects.Value.LoadRequested)
        {
            await LoadAsync(ct);
        }
    }
}
