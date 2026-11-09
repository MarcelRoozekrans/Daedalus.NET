using Daedalus.Agents.Channels;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.ChannelOutbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeroAlloc.Outbox;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     The one mutating action this phase offers on a scheduled run's delivery: requeueing a dead-lettered
///     message. Deliberately its own type behind <see cref="IScheduleDeliveryActions"/> rather than a method on
///     <see cref="ScheduleDiagnostics"/> — see that interface's remarks for why a read-only seam must stay
///     read-only.
/// </summary>
/// <remarks>
///     <para>
///     <b>Scoped, because the channel pipeline's <see cref="IOutboxRequeue"/> is.</b> The requeue is the
///     ZeroAlloc.Outbox.Orm store of the pipeline <see cref="ChannelOutboxServiceCollectionExtensions.PipelineName"/>,
///     registered scoped and keyed by that name. The read opens its own pooled connection and closes it before the
///     requeue runs.
///     </para>
///     <para>
///     <b>Execution id to outbox row is a scan, not a lookup, for the same reason
///     <see cref="ScheduleDiagnostics"/>'s dead-letter read is.</b> <c>OutboxMessages</c> has no correlation
///     column; <see cref="ChannelMessageQueued.ExecutionId"/> is inside the opaque payload, so the
///     type-and-status-narrowed candidates must be deserialized in memory to find the one that matches. This
///     reuses <see cref="ScheduleDiagnosticsOptions"/>'s bounds rather than inventing a second set: whatever
///     window found this execution's dead letter and reported it <see cref="Application.DTOs.Scheduling.RunVerdict.Undelivered"/>
///     is exactly the window that will find it again here.
///     </para>
///     <para>
///     <b>The store has no read, so the scan is a query of its own.</b> <see cref="ChannelOutboxSql"/> reads the
///     candidates from the pipeline's table; the requeue goes through the store's <see cref="IOutboxRequeue"/>, which
///     moves a dead letter back to pending, due now, with a fresh attempt counter and no lease. It throws when the
///     message is gone or no longer dead-lettered, such as when a second resend raced this one, and that failure is
///     reported, not thrown.
///     </para>
/// </remarks>
/// <param name="db">Opens the connection the candidate scan reads on.</param>
/// <param name="timeProvider">The only clock this type reads. <c>DateTimeOffset.UtcNow</c> appears nowhere.</param>
/// <param name="requeue">The channel pipeline's requeue, which resends the resolved outbox row once it is found.</param>
/// <param name="serializer">The same serializer the host registers, so no wire format is assumed here.</param>
/// <param name="options">Reuses <see cref="ScheduleDiagnosticsOptions"/>'s dead-letter scan bounds.</param>
/// <param name="logger">Records a skipped, unreadable candidate row.</param>
public sealed class ScheduleDeliveryActions(
    DaedalusDb db,
    TimeProvider timeProvider,
    [FromKeyedServices(ChannelOutboxServiceCollectionExtensions.PipelineName)] IOutboxRequeue requeue,
    IOutboxSerializer serializer,
    IOptions<ScheduleDiagnosticsOptions> options,
    ILogger<ScheduleDeliveryActions> logger) : IScheduleDeliveryActions
{
    private static readonly string _channelMessageTypeName =
        typeof(ChannelMessageQueued).FullName
        ?? throw new InvalidOperationException($"{nameof(ChannelMessageQueued)} has no full name; it cannot be a generic parameter or array type.");

    private readonly ScheduleDiagnosticsOptions _options = options.Value;

    /// <inheritdoc/>
    public async Task<Result> RequeueAsync(Guid executionId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var since = ScheduleDiagnostics.WindowStart(_options.DeadLetterLookback, now);

        var candidates = await ReadCandidatesAsync(since, ct).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            ChannelMessageQueued? message;
            try
            {
                message = serializer.Deserialize<ChannelMessageQueued>(candidate.Payload);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Skipping a dead-lettered {TypeName} row whose payload could not be deserialized while resolving a resend for execution {ExecutionId}.",
                    _channelMessageTypeName, executionId);
                continue;
            }

            if (message?.ExecutionId != executionId)
            {
                continue;
            }

            try
            {
                await requeue.RequeueAsync(new OutboxMessageId(candidate.Id), ct).ConfigureAwait(false);
                return Result.Success();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Requeue failed for execution {ExecutionId}, outbox message {MessageId}.", executionId, candidate.Id);
                return Result.Failure($"Requeue failed: {ex.Message}");
            }
        }

        return Result.Failure(
            $"No dead-lettered message was found for execution {executionId}. It may already have been resolved.");
    }

    /// <summary>
    ///     The dead-lettered channel messages in the scan window, newest first. The connection is closed before the
    ///     caller requeues, so the scan holds no connection while the store opens its own.
    /// </summary>
    private async Task<IReadOnlyList<ChannelOutboxDeadLetter>> ReadCandidatesAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        return await new ChannelOutboxSql(connection)
            .ListDeadLettersAsync(_channelMessageTypeName, since, _options.DeadLetterScanLimit, ct)
            .ConfigureAwait(false);
    }
}
