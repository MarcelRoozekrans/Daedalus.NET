namespace Daedalus.Domain.Entities;

/// <summary>
///     Lifecycle state of a Thalos agent session. Mirror of <c>Thalos.SessionState</c> kept in Domain
///     so Domain stays framework-free; integer values must match one-to-one (asserted by an integration test).
/// </summary>
public enum AgentSessionState
{
    /// <summary>No turn in progress; accepts a new turn.</summary>
    Idle = 0,

    /// <summary>A turn is executing.</summary>
    Running = 1,

    /// <summary>A tool call awaits human approval.</summary>
    AwaitingApproval = 2,

    /// <summary>Terminal: the session accepts no more turns.</summary>
    Closed = 3,
}
