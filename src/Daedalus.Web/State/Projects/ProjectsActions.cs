namespace Daedalus.Web.State.Projects;

/// <summary>A projects load was started, numbered <paramref name="Sequence"/>.</summary>
public readonly record struct ProjectsLoadStarted(long Sequence);

/// <summary>The projects arrived for load <paramref name="Sequence"/>.</summary>
public readonly record struct ProjectsLoaded(long Sequence, IReadOnlyList<ProjectDto> Projects);

/// <summary>Load <paramref name="Sequence"/> failed with <paramref name="Error"/>.</summary>
public readonly record struct ProjectsLoadFailed(long Sequence, string Error);

/// <summary>
///     Load <paramref name="Sequence"/> ended with nothing to show: its reader went away, or the session is being renewed
///     and the browser is on its way to sign in.
/// </summary>
public readonly record struct ProjectsLoadAbandoned(long Sequence);
