#pragma warning disable CA1819 // Use byte[] instead of property returning array (EF Core concurrency token standard pattern)
#pragma warning disable S1144 // EF Core sets TasksCompleted and RowVersion via reflection (unused private setters are required)

using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     Represents a worker session executing Ralph loops.
///     Used for distributed task claiming and session health tracking.
/// </summary>
public sealed class ExecutionSession : AggregateRoot<Guid>
{
    /// <summary>Gets the human-readable worker name (e.g., "worker-001").</summary>
    public string WorkerName { get; private set; } = string.Empty;

    /// <summary>Gets when the session started.</summary>
    public DateTime StartedAt { get; private set; } = DateTime.UtcNow;

    /// <summary>Gets the last heartbeat timestamp.</summary>
    public DateTime LastHeartbeat { get; private set; } = DateTime.UtcNow;

    /// <summary>Gets whether the session is still active.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>Gets the number of tasks completed by this session.</summary>
    public int TasksCompleted { get; private set; }

    /// <summary>
    ///     Gets the concurrency token (row version) for optimistic locking.
    ///     Prevents lost updates in concurrent scenarios (e.g., heartbeat vs. shutdown).
    /// </summary>
    public byte[]? RowVersion { get; private set; }

    /// <summary>
    ///     Creates a new execution session.
    /// </summary>
    public static Result<ExecutionSession> Create(Guid id, string workerName, DateTime? startedAt = null)
    {
        if (string.IsNullOrWhiteSpace(workerName))
        {
            return Result.Failure<ExecutionSession>("Worker name cannot be empty");
        }

        var now = startedAt ?? DateTime.UtcNow;
        return Result.Success(new ExecutionSession
        {
            Id = id,
            WorkerName = workerName.Trim(),
            StartedAt = now,
            LastHeartbeat = now
        });
    }

    /// <summary>
    ///     Records that a task was completed by this session, incrementing the counter.
    /// </summary>
    public void RecordTaskCompleted() => TasksCompleted++;

    /// <summary>
    ///     Updates the heartbeat timestamp to signal the session is still alive.
    /// </summary>
    public void Heartbeat(DateTime? heartbeatTime = null) => LastHeartbeat = heartbeatTime ?? DateTime.UtcNow;

    /// <summary>
    ///     Marks the session as inactive when shutting down gracefully.
    /// </summary>
    public void Shutdown() => IsActive = false;

    /// <summary>
    ///     Checks if this session has staled (no heartbeat for specified duration).
    /// </summary>
    public bool IsStale(TimeSpan staleness, DateTime? currentTime = null)
    {
        var now = currentTime ?? DateTime.UtcNow;
        return now - LastHeartbeat > staleness;
    }
}
#pragma warning restore S1144
#pragma warning restore CA1819
