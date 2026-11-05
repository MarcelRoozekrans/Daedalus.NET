namespace Daedalus.Application.Services;

/// <summary>
///     The refusal of a guarded save that lost a race to another write of the same row: "The &lt;entity&gt; was modified
///     by another operation. Please retry." The endpoints map a failure that carries <see cref="Marker"/> to 409
///     Conflict, as <c>RepositoriesEndpoints</c> and <c>ProjectsEndpoints</c> do.
/// </summary>
public static class SaveConflict
{
    /// <summary>The words every lost-race refusal carries.</summary>
    public const string Marker = "was modified by another operation";

    /// <summary>Whether <paramref name="error"/> is the refusal of a save that lost a race, which maps to 409.</summary>
    /// <param name="error">A failure's text.</param>
    /// <returns>True when the text carries <see cref="Marker"/>, in any case, as RepositoriesEndpoints matched it.</returns>
    public static bool IsConflict(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Contains(Marker, StringComparison.OrdinalIgnoreCase);
    }
}
