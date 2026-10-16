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

    /// <summary>Creates a project; the outcome is <see cref="ProjectsState.LastSave"/>.</summary>
    public async Task CreateAsync(CreateProjectDto project, CancellationToken ct)
    {
        await dispatcher.DispatchAsync(new ProjectSaveStarted(ProjectWrite.Created));
        var result = await writes.CreateProjectAsync(project, ct);
        await SettleAsync(ProjectWrite.Created, result, created => created.Id, ct);
    }

    /// <summary>Updates project <paramref name="id"/>; the outcome is <see cref="ProjectsState.LastSave"/>.</summary>
    public async Task UpdateAsync(Guid id, UpdateProjectDto project, CancellationToken ct)
    {
        await dispatcher.DispatchAsync(new ProjectSaveStarted(ProjectWrite.Updated));
        var result = await writes.UpdateProjectAsync(id, project, ct);
        await SettleAsync(ProjectWrite.Updated, result, _ => id, ct);
    }

    /// <summary>
    ///     Deletes project <paramref name="id"/>; the outcome is <see cref="ProjectsState.LastSave"/>, and the page
    ///     stops showing it if it was shown.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await dispatcher.DispatchAsync(new ProjectSaveStarted(ProjectWrite.Deleted));
        var result = await writes.DeleteProjectAsync(id, ct);
        await SettleAsync(ProjectWrite.Deleted, result, _ => id, ct);
    }

    /// <summary>
    ///     Dispatches a write's outcome: saved, with the project's id, or refused with the reason. A
    ///     <see cref="SessionExpiry.SigningInAgain"/> failure, or one after <paramref name="ct"/> was cancelled, is no
    ///     outcome: the session is being renewed, or the page that asked is gone.
    /// </summary>
    private async Task SettleAsync<T>(ProjectWrite write, Result<T> result, Func<T, Guid> id, CancellationToken ct)
    {
        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new ProjectSaved(write, id(result.Value)));
        }
        else if (!SessionExpiry.IsSigningIn(result.Error) && !ct.IsCancellationRequested)
        {
            var reason = result.Error ?? $"Failed to {Verb(write)}";
            LogWriteRefused(logger, write, reason);
            await dispatcher.DispatchAsync(new ProjectSaveRefused(write, reason));
        }
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
