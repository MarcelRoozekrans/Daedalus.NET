using System.Threading.Channels;
using Daedalus.Application.DTOs.WorkflowRuns;
using Microsoft.AspNetCore.SignalR;
using Thalos.Workflow;

namespace Daedalus.Api.Hubs;

/// <summary>
///     The store's observer and the hub's sender in one. The observer only queues, into a bounded channel that drops
///     the oldest message when full, so the store never waits on a socket; the background loop sends. A dropped
///     message costs nothing: clients re-fetch on the next change and on every reconnect.
/// </summary>
public sealed partial class WorkflowRunChangeBroadcaster(
    IHubContext<WorkflowRunsHub> hub, ILogger<WorkflowRunChangeBroadcaster> logger) : BackgroundService, IWorkflowRunObserver
{
    /// <summary>How many changes wait for the loop before the oldest is dropped.</summary>
    private const int Capacity = 256;

    private readonly Channel<WorkflowRunChanged> _changes = Channel.CreateBounded<WorkflowRunChanged>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /// <inheritdoc/>
    public ValueTask OnChangedAsync(WorkflowRunChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        _changes.Writer.TryWrite(new WorkflowRunChanged(change.RunId, change.Status.ToString(), change.CurrentNode, change.AwaitingSignal));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _changes.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await hub.Clients.All.SendAsync(WorkflowRunsHub.RunChangedMethod, message, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // Only a stop ends the loop: one failed send, a cancelled one included, never stops the next.
                LogSendFailed(logger, message.Id, exception.GetType().FullName ?? exception.GetType().Name);
            }
        }
    }

    [LoggerMessage(EventId = 2364, Level = LogLevel.Warning,
        Message = "Sending the change of run {RunId} to the console failed with {ExceptionType}. Clients re-fetch on the next change.")]
    private static partial void LogSendFailed(ILogger logger, Guid runId, string exceptionType);
}
