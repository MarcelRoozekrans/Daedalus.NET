#pragma warning disable CA1819 // EF Core concurrency token standard pattern
#pragma warning disable S1144 // EF Core sets RowVersion via reflection

using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     Header of a Thalos agent conversation. Messages live in <see cref="AgentMessage"/>.
///     Timestamps are supplied by the caller (the adapter injects a <c>TimeProvider</c>) so the aggregate stays deterministic.
/// </summary>
public sealed class AgentSession : AggregateRoot<Guid>
{
    /// <summary>Gets the agent definition this session runs.</summary>
    public Guid AgentId { get; private set; }

    /// <summary>Gets the owner (user id / subject) of the session.</summary>
    public string OwnerId { get; private set; } = string.Empty;

    /// <summary>Gets the lifecycle state.</summary>
    public AgentSessionState State { get; private set; }

    /// <summary>Gets when the session was created (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Gets when the session was last touched (UTC).</summary>
    public DateTime LastActivityAt { get; private set; }

    /// <summary>Gets the number of completed turns.</summary>
    public int TurnCount { get; private set; }

    /// <summary>Gets the accumulated input tokens over all turns.</summary>
    public long TotalInputTokens { get; private set; }

    /// <summary>Gets the accumulated output tokens over all turns.</summary>
    public long TotalOutputTokens { get; private set; }

    /// <summary>Gets the concurrency token (row version) for optimistic locking.</summary>
    public byte[]? RowVersion { get; private set; }

    private AgentSession() { } // EF Core

    /// <summary>Creates a new idle session with zero counters.</summary>
    /// <param name="id">The session id (supplied by the adapter; Thalos typed ids are Guid-backed).</param>
    /// <param name="agentId">The agent definition id.</param>
    /// <param name="ownerId">The owner subject.</param>
    /// <param name="utcNow">The creation timestamp (UTC).</param>
    /// <returns>A Result containing the new session or an error.</returns>
    public static Result<AgentSession> Create(Guid id, Guid agentId, string ownerId, DateTime utcNow)
    {
        if (id == Guid.Empty)
            return Result.Failure<AgentSession>("Session id is required.");

        if (agentId == Guid.Empty)
            return Result.Failure<AgentSession>("Agent id is required.");

        if (string.IsNullOrWhiteSpace(ownerId))
            return Result.Failure<AgentSession>("Owner id is required.");

        return Result.Success(new AgentSession
        {
            Id = id,
            AgentId = agentId,
            OwnerId = ownerId,
            State = AgentSessionState.Idle,
            CreatedAt = utcNow,
            LastActivityAt = utcNow,
        });
    }

    /// <summary>Increments the turn counter, accumulates token usage and bumps <see cref="LastActivityAt"/>.</summary>
    /// <param name="inputTokens">Input tokens consumed by the turn.</param>
    /// <param name="outputTokens">Output tokens produced by the turn.</param>
    /// <param name="utcNow">The activity timestamp (UTC).</param>
    public void RecordTurn(int inputTokens, int outputTokens, DateTime utcNow)
    {
        TurnCount++;
        TotalInputTokens += inputTokens;
        TotalOutputTokens += outputTokens;
        LastActivityAt = utcNow;
    }

    /// <summary>Sets the lifecycle state unconditionally (transition validation is the runtime's job) and bumps <see cref="LastActivityAt"/>.</summary>
    /// <param name="state">The new state.</param>
    /// <param name="utcNow">The activity timestamp (UTC).</param>
    public void SetState(AgentSessionState state, DateTime utcNow)
    {
        State = state;
        LastActivityAt = utcNow;
    }
}

#pragma warning restore S1144
#pragma warning restore CA1819
