namespace Daedalus.Web.State.Projects;

/// <summary>Which write a <see cref="ProjectSave"/> is the outcome of.</summary>
public enum ProjectWrite
{
    Created,
    Updated,
    Deleted,
}

/// <summary>
///     The outcome of a project write: saved when <paramref name="Refusal"/> is <see langword="null"/>, refused with the
///     reason otherwise. The component that asked shows it; the effect never does.
/// </summary>
public readonly record struct ProjectSave(ProjectWrite Write, string? Refusal)
{
    /// <summary>Whether the write was saved.</summary>
    public bool Succeeded => Refusal is null;
}
