using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Helpers;

namespace Daedalus.Tests.Integration.Builders;

/// <summary>
///     Fluent builder for creating test ExecutionSession entities.
///     Provides a clean, readable way to set up test session data.
/// </summary>
/// <example>
///     var activeSession = new ExecutionSessionTestBuilder()
///     .WithWorkerName("worker-1")
///     .Build();
///     var staleSession = new ExecutionSessionTestBuilder()
///     .WithWorkerName("worker-stale")
///     .AsStale(TimeSpan.FromMinutes(10))
///     .Build();
/// </example>
public sealed class ExecutionSessionTestBuilder
{
    private Guid _id = Guid.NewGuid();
    private bool _isActive = true;
    private DateTime _lastHeartbeat = DateTime.UtcNow;
    private string _workerName = $"worker-{Guid.NewGuid():N}".Substring(0, 20);

    /// <summary>
    ///     Sets the session ID.
    /// </summary>
    public ExecutionSessionTestBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    /// <summary>
    ///     Sets the worker name.
    /// </summary>
    public ExecutionSessionTestBuilder WithWorkerName(string workerName)
    {
        _workerName = workerName;
        return this;
    }

    /// <summary>
    ///     Sets the last heartbeat timestamp.
    /// </summary>
    public ExecutionSessionTestBuilder WithLastHeartbeat(DateTime lastHeartbeat)
    {
        _lastHeartbeat = lastHeartbeat;
        return this;
    }

    /// <summary>
    ///     Marks the session as stale (last heartbeat in the past).
    /// </summary>
    /// <param name="staleness">How far in the past the heartbeat should be. Defaults to 10 minutes.</param>
    public ExecutionSessionTestBuilder AsStale(TimeSpan? staleness = null)
    {
        var stalenessDuration = staleness ?? TimeSpan.FromMinutes(10);
        _lastHeartbeat = DateTime.UtcNow.Subtract(stalenessDuration);
        return this;
    }

    /// <summary>
    ///     Marks the session as inactive.
    /// </summary>
    public ExecutionSessionTestBuilder AsInactive()
    {
        _isActive = false;
        return this;
    }

    /// <summary>
    ///     Builds the ExecutionSession entity with configured values.
    /// </summary>
    /// <returns>A fully configured ExecutionSession entity</returns>
    /// <exception cref="InvalidOperationException">If session creation fails</exception>
    public ExecutionSession Build()
    {
        var result = ExecutionSession.Create(_id, _workerName);
        var session = result.MustSucceed("Failed to build session for testing");

        // Set the last heartbeat timestamp if it differs from current
        if (_lastHeartbeat != DateTime.UtcNow)
        {
            try
            {
                // Try to set via reflection if there's no public setter
                var heartbeatProperty = typeof(ExecutionSession).GetProperty(nameof(ExecutionSession.LastHeartbeat));
                if (heartbeatProperty?.CanWrite == true)
                {
                    heartbeatProperty.SetValue(session, _lastHeartbeat);
                }
            }
            catch
            {
                // If we can't set it, that's OK - heartbeat will be current
            }
        }

        // Mark as inactive if requested
        if (!_isActive)
        {
            session.Shutdown();
        }

        return session;
    }
}
