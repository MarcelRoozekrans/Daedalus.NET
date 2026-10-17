using Thalos;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Collects the usage of every agent turn one workflow node dispatch runs, so a node whose turn failed can still be
///     recorded: Thalos reports a failed turn's usage only on its <see cref="TurnFailedNotification"/>, never on the
///     <see cref="AgentError"/> the dispatcher receives. <see cref="FailedNodeUsageRunner"/> opens one around the runner it
///     wraps, and <see cref="TurnUsageNotificationPublisher"/> adds each turn's usage to the one open on its async flow.
/// </summary>
/// <remarks>
///     <see cref="Current"/> is an <see cref="AsyncLocal{T}"/>, so two nodes dispatched at once each collect their own
///     turns. Thalos' runtime publishes a turn's notification from the turn's producer task, which starts inside the call
///     the scope was opened around and so inherits it; the turns are added to the scope object, which is shared, never by
///     setting <see cref="Current"/> from there.
/// </remarks>
internal sealed class NodeTurnUsageScope : IDisposable
{
    private static readonly AsyncLocal<NodeTurnUsageScope?> _current = new();

    private readonly NodeTurnUsageScope? _previous;
    private readonly List<TurnUsage> _turns = [];
    private readonly Lock _gate = new();

    private NodeTurnUsageScope(NodeTurnUsageScope? previous) => _previous = previous;

    /// <summary>The scope open on this async flow, or <see langword="null"/> outside any workflow node dispatch.</summary>
    public static NodeTurnUsageScope? Current => _current.Value;

    /// <summary>The usage of each turn added so far, in the order the turns reported it.</summary>
    public IReadOnlyList<TurnUsage> Turns
    {
        get
        {
            lock (_gate)
            {
                return [.. _turns];
            }
        }
    }

    /// <summary>Opens a scope and makes it <see cref="Current"/>; disposing it restores the scope that was current before.</summary>
    public static NodeTurnUsageScope Begin()
    {
        var scope = new NodeTurnUsageScope(_current.Value);
        _current.Value = scope;
        return scope;
    }

    /// <summary>Adds one turn's usage. Thread-safe.</summary>
    public void Add(TurnUsage usage)
    {
        lock (_gate)
        {
            _turns.Add(usage);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _current.Value = _previous;
}
