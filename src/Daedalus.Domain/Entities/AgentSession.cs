using ZeroAlloc.Results;

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

    private AgentSession() { }

    /// <summary>Creates a new idle session with zero counters.</summary>
    /// <param name="id">The session id (supplied by the adapter; Thalos typed ids are Guid-backed).</param>
    /// <param name="agentId">The agent definition id.</param>
    /// <param name="ownerId">The owner subject.</param>
    /// <param name="utcNow">The creation timestamp (UTC).</param>
    /// <returns>A Result containing the new session or an error.</returns>
    public static Result<AgentSession> Create(Guid id, Guid agentId, string ownerId, DateTime utcNow)
    {
        if (id == Guid.Empty)
            return Result<AgentSession>.Failure("Session id is required.");

        if (agentId == Guid.Empty)
            return Result<AgentSession>.Failure("Agent id is required.");

        if (string.IsNullOrWhiteSpace(ownerId))
            return Result<AgentSession>.Failure("Owner id is required.");

        return Result<AgentSession>.Success(new AgentSession
        {
            Id = id,
            AgentId = agentId,
            OwnerId = ownerId,
            State = AgentSessionState.Idle,
            CreatedAt = utcNow,
            LastActivityAt = utcNow,
        });
    }

    /// <summary>
    ///     Rebuilds a stored session as it is, for persistence only: validates nothing and raises no domain events, so a
    ///     row <see cref="Create"/> would refuse today still loads.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <param name="agentId">The agent definition id.</param>
    /// <param name="ownerId">The owner subject.</param>
    /// <param name="state">The lifecycle state.</param>
    /// <param name="createdAt">When the session was created (UTC).</param>
    /// <param name="lastActivityAt">When the session was last touched (UTC).</param>
    /// <param name="turnCount">The number of completed turns.</param>
    /// <param name="totalInputTokens">The accumulated input tokens.</param>
    /// <param name="totalOutputTokens">The accumulated output tokens.</param>
    /// <returns>The session.</returns>
    public static AgentSession Rehydrate(
        Guid id,
        Guid agentId,
        string ownerId,
        AgentSessionState state,
        DateTime createdAt,
        DateTime lastActivityAt,
        int turnCount,
        long totalInputTokens,
        long totalOutputTokens) => new()
        {
            Id = id,
            AgentId = agentId,
            OwnerId = ownerId,
            State = state,
            CreatedAt = createdAt,
            LastActivityAt = lastActivityAt,
            TurnCount = turnCount,
            TotalInputTokens = totalInputTokens,
            TotalOutputTokens = totalOutputTokens,
        };

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
