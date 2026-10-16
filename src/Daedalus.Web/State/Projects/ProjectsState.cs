using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Projects;

/// <summary>
///     The projects, the one list the Tasks page's Project column, the task create dialog's picker and Home's New task
///     read.
///     <para>
///         Lifetime: never cleared when a page closes. The feature is shared, so a clear from the page being left would
///         race the next one: Blazor disposes the page being left after it initialises the next, and a clear would land
///         after the new page asked for the projects. The list is small and every reader re-fetches it when it opens,
///         so what a closed page leaves behind is shown at most until that re-fetch lands. A response is kept only when
///         no newer load has started since its own, by <paramref name="Sequence"/>, so an earlier reader's late answer
///         never replaces a later one.
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
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct ProjectsState(
    IReadOnlyList<ProjectDto>? Projects, IReadOnlyDictionary<Guid, string> Names, string? Error, bool Loading, long Sequence,
    bool LoadRequested)
{
    /// <summary>The state before any load.</summary>
    public static ProjectsState Initial(IServiceProvider services) =>
        new(null, new Dictionary<Guid, string>(), null, false, 0, false);
}
