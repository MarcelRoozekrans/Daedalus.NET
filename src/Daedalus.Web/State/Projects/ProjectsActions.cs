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

/// <summary>The Projects page asked for project <paramref name="Id"/> with its tasks.</summary>
public readonly record struct ProjectSelectRequested(Guid Id);

/// <summary><paramref name="Project"/> arrived with its tasks.</summary>
public readonly record struct ProjectSelectLoaded(ProjectDto Project);

/// <summary>Loading project <paramref name="Id"/> with its tasks failed with <paramref name="Error"/>.</summary>
public readonly record struct ProjectSelectFailed(Guid Id, string Error);

/// <summary>Loading project <paramref name="Id"/> ended with the session being renewed: nothing to show.</summary>
public readonly record struct ProjectSelectAbandoned(Guid Id);

/// <summary>A <paramref name="Write"/> was started.</summary>
public readonly record struct ProjectSaveStarted(ProjectWrite Write);

/// <summary>The <paramref name="Write"/> of project <paramref name="Id"/> was saved.</summary>
public readonly record struct ProjectSaved(ProjectWrite Write, Guid Id);

/// <summary>The <paramref name="Write"/> was refused for <paramref name="Reason"/>.</summary>
public readonly record struct ProjectSaveRefused(ProjectWrite Write, string Reason);

/// <summary>The Projects page closed: its selection and last save go; the shared list stays.</summary>
public readonly record struct ProjectsPageClosed;
