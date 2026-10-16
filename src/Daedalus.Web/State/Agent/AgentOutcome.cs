namespace Daedalus.Web.State.Agent;

/// <summary>
///     The outcome of an Agent page request: done when <paramref name="Refusal"/> is <see langword="null"/>, refused
///     otherwise, with the API's reason. The page shows a refusal as a toast; the effect never does, and it is not state.
/// </summary>
/// <param name="Refusal">Why the API refused, or <see langword="null"/>.</param>
/// <param name="SessionId">The session a create made, for the page to open; <see langword="null"/> for every other request.</param>
public readonly record struct AgentOutcome(string? Refusal, string? SessionId = null)
{
    /// <summary>The outcome of a request the API accepted.</summary>
    public static AgentOutcome Done { get; } = new(null);

    /// <summary>Whether the API accepted the request.</summary>
    public bool Succeeded => Refusal is null;
}
