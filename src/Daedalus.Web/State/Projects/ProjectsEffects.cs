using Daedalus.Web.Auth;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Projects;

/// <summary>
///     The projects feature's I/O: the one load the Tasks page, the task create dialog and Home's New task share. Each
///     reader calls <see cref="LoadAsync"/> when it opens; see <see cref="ProjectsState"/> for why nothing clears the
///     list. An expected failure is an action, never an exception. A session being renewed and a load whose reader went
///     away store no error. A failure that is stored is logged as a warning, with the error text only.
/// </summary>
public sealed partial class ProjectsEffects(
    ApiClient api, IProjectApiClient writes, IDispatcher dispatcher, IStore<ProjectsState> projects,
    ILogger<ProjectsEffects> logger)
{
    /// <summary>Every reader reads one page of this many projects, the size the Tasks page has always read.</summary>
    public const int Page = 200;

    /// <summary>
    ///     Fetches the projects. A failure is <see cref="ProjectsState.Error"/>; a <see cref="SessionExpiry.SigningInAgain"/>
    ///     failure, or one after <paramref name="ct"/> was cancelled, only ends the load.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        // A caller already gone, such as a reader that closed before its reload, dispatches nothing: a start would
        // clear what a page opened since shows and move the sequence past its answer.
        if (ct.IsCancellationRequested)
        {
            return;
        }

        // Read and dispatched with no await between: the dispatch reduces at once, on the one browser thread, so the
        // next load reads this one's number and takes the one after it.
        var sequence = projects.Value.Sequence + 1;
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
            LogLoadFailed(logger, result.Error);
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

    /// <summary>
    ///     Fetches project <paramref name="id"/> with its tasks, for the Projects page. A failure is
    ///     <see cref="ProjectsState.SelectError"/>; a <see cref="SessionExpiry.SigningInAgain"/> failure, or one after
    ///     <paramref name="ct"/> was cancelled, shows no project and stores no error.
    /// </summary>
    public async Task LoadSelectedAsync(Guid id, CancellationToken ct)
    {
        // A page already gone dispatches nothing: its request would move SelectedId off the reopened page's project.
        if (ct.IsCancellationRequested)
        {
            return;
        }

        await dispatcher.DispatchAsync(new ProjectSelectRequested(id));
        var result = await api.GetProjectWithTasksAsync(id, ct);
        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new ProjectSelectLoaded(result.Value));
        }
        else if (SessionExpiry.IsSigningIn(result.Error) || ct.IsCancellationRequested)
        {
            await dispatcher.DispatchAsync(new ProjectSelectAbandoned(id));
        }
        else
        {
            LogSelectFailed(logger, result.Error);
            await dispatcher.DispatchAsync(new ProjectSelectFailed(id, result.Error));
        }
    }

    /// <summary>Creates a project; returns the outcome; see <see cref="SettleAsync{T}"/>.</summary>
    public async Task<ProjectSave?> CreateAsync(CreateProjectDto project, CancellationToken ct)
    {
        var result = await writes.CreateProjectAsync(project, ct);
        return await SettleAsync(ProjectWrite.Created, result, created => created.Id, ct);
    }

    /// <summary>Updates project <paramref name="id"/>; returns the outcome; see <see cref="SettleAsync{T}"/>.</summary>
    public async Task<ProjectSave?> UpdateAsync(Guid id, UpdateProjectDto project, CancellationToken ct)
    {
        var result = await writes.UpdateProjectAsync(id, project, ct);
        return await SettleAsync(ProjectWrite.Updated, result, _ => id, ct);
    }

    /// <summary>
    ///     Deletes project <paramref name="id"/>; returns the outcome; see <see cref="SettleAsync{T}"/>, and the page
    ///     stops showing it if it was shown.
    /// </summary>
    public async Task<ProjectSave?> DeleteAsync(Guid id, CancellationToken ct)
    {
        var result = await writes.DeleteProjectAsync(id, ct);
        return await SettleAsync(ProjectWrite.Deleted, result, _ => id, ct);
    }

    /// <summary>
    ///     Returns a write's outcome: saved, or refused with the reason. A save is dispatched with the project's id, so a
    ///     deleted project the page shows is shown no longer. A <see cref="SessionExpiry.SigningInAgain"/> failure, or one
    ///     after <paramref name="ct"/> was cancelled, is no outcome, <see langword="null"/>: the session is being renewed,
    ///     or the page that asked is gone. The outcome is not stored: the caller shows what its own write returned, so it
    ///     never shows another write's outcome.
    /// </summary>
    private async Task<ProjectSave?> SettleAsync<T>(ProjectWrite write, Result<T> result, Func<T, Guid> id, CancellationToken ct)
    {
        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new ProjectSaved(write, id(result.Value)));
            return new ProjectSave(write, null);
        }

        if (SessionExpiry.IsSigningIn(result.Error) || ct.IsCancellationRequested)
        {
            return null;
        }

        var reason = result.Error ?? $"Failed to {Verb(write)}";
        LogWriteRefused(logger, write, reason);
        return new ProjectSave(write, reason);
    }

    private static string Verb(ProjectWrite write) => write switch
    {
        ProjectWrite.Created => "create",
        ProjectWrite.Updated => "update",
        _ => "delete",
    };

    [LoggerMessage(EventId = 3200, Level = LogLevel.Warning, Message = "The projects could not be loaded: {Error}")]
    private static partial void LogLoadFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3201, Level = LogLevel.Warning, Message = "A project with its tasks could not be loaded: {Error}")]
    private static partial void LogSelectFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Warning, Message = "A project write ({Write}) was refused: {Reason}")]
    private static partial void LogWriteRefused(ILogger logger, ProjectWrite write, string reason);
}
