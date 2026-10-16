namespace Daedalus.Web.State.Brainstorm;

/// <summary>
///     The outcome of a Brainstorm request: done when <paramref name="Refusal"/> is <see langword="null"/>, refused
///     otherwise, with the API's reason. The page shows a refusal as a toast; the effect never does, and it is not state.
/// </summary>
/// <param name="Refusal">Why the API refused, or <see langword="null"/>.</param>
/// <param name="TasksCreated">How many tasks a task generation created; 0 for every other request.</param>
public readonly record struct BrainstormOutcome(string? Refusal, int TasksCreated)
{
    /// <summary>The outcome of a request the API accepted.</summary>
    public static BrainstormOutcome Done { get; } = new(null, 0);

    /// <summary>Whether the API accepted the request.</summary>
    public bool Succeeded => Refusal is null;
}
