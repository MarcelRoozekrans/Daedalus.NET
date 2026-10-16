using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Projects;

/// <summary>
///     The projects, the one list the Projects page, the Tasks page's Project column, the task create dialog's picker,
///     Home's New task and the PRD generator's project step read; and, for the Projects page alone, the project it shows
///     with its tasks and the outcome of its last save.
///     <para>
///         Lifetime: never cleared when a page closes. The feature is shared, so a clear from the page being left would
///         race the next one: Blazor disposes the page being left after it initialises the next, and a clear would land
///         after the new page asked for the projects. The list is small and every reader re-fetches it when it opens,
///         so what a closed page leaves behind is shown at most until that re-fetch lands. A response is kept only when
///         no newer load has started since its own, by <paramref name="Sequence"/>, so an earlier reader's late answer
///         never replaces a later one.
///     </para>
///     <para>
///         <paramref name="Selected"/>, <paramref name="SelectedId"/>, <paramref name="SelectError"/> and
///         <paramref name="LastSave"/> are the Projects page's own, and page-only: cleared by
///         <see cref="ProjectsPageClosed"/>, which leaves the list alone. No other reader reads them, so their clear
///         cannot race another page's open the way a clear of the list would.
///     </para>
/// </summary>
/// <param name="Projects">The projects, or <see langword="null"/> until a load has succeeded.</param>
/// <param name="Names">Each project's name by its id, for the board's Project column.</param>
/// <param name="Error">
///     Why the last load failed, or <see langword="null"/>. Never <see cref="Auth.SessionExpiry.SigningInAgain"/>, and
///     never a cancellation: neither is stored.
/// </param>
/// <param name="Loading">Whether the newest load is in flight.</param>
/// <param name="Sequence">The sequence number of the newest load started; an answer to an older one is dropped.</param>
/// <param name="LoadRequested">
///     Whether a load was ever started, whatever came of it. A reconnect re-fetches the projects only then.
/// </param>
/// <param name="Selected">The project the Projects page shows with its tasks, or <see langword="null"/>.</param>
/// <param name="SelectedId">
///     The project the Projects page asked for last, or <see langword="null"/>. An answer for any other project is
///     dropped, so a late answer to an earlier click never replaces a later one.
/// </param>
/// <param name="SelectError">
///     Why loading <paramref name="SelectedId"/> failed, or <see langword="null"/>. Never
///     <see cref="Auth.SessionExpiry.SigningInAgain"/>.
/// </param>
/// <param name="LastSave">
///     The outcome of the last create, update or delete, or <see langword="null"/> while one is in flight, before any,
///     and after one that ended with the session being renewed or its reader gone: neither is an outcome to show. A
///     dialog's write is not cancelled by the page closing, so its outcome can land after
///     <see cref="ProjectsPageClosed"/> and stay until the next write starts. It is read only by the component that
///     awaited that write, at once and by its <see cref="ProjectSave.Write"/>, so a left-over outcome is never shown.
/// </param>
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct ProjectsState(
    IReadOnlyList<ProjectDto>? Projects, IReadOnlyDictionary<Guid, string> Names, string? Error, bool Loading, long Sequence,
    bool LoadRequested, ProjectDto? Selected, Guid? SelectedId, string? SelectError, ProjectSave? LastSave)
{
    /// <summary>
    ///     The projects once the newest load has finished without an error, or <see langword="null"/> while one is in
    ///     flight, after it failed, or before any succeeded. A decision made once, such as the create dialog's
    ///     one-project preselect, waits for this, so it is never made on a list a closed page left behind, nor on one a
    ///     failed refresh could not confirm.
    /// </summary>
    public IReadOnlyList<ProjectDto>? Settled => Loading || Error is not null ? null : Projects;

    /// <summary>
    ///     The projects a list shows outside a load of its own: the held list while a background refresh, such as a
    ///     reconnect's, is in flight, so that refresh shows no spinner and does not empty the list; and
    ///     <see langword="null"/> once a load has failed, so the failure shows as it always did, never over a held list.
    ///     The Projects page and the PRD project step show their spinner only for a load they started.
    /// </summary>
    public IReadOnlyList<ProjectDto>? Current => Error is null ? Projects : null;

    /// <summary>The state before any load.</summary>
    public static ProjectsState Initial(IServiceProvider services) =>
        new(null, new Dictionary<Guid, string>(), null, false, 0, false, null, null, null, null);
}
