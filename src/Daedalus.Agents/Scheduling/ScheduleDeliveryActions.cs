using Daedalus.Agents.Channels;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Persistence;
using CSharpFunctionalExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     The one mutating action this phase offers on a scheduled run's delivery: requeueing a dead-lettered
///     message. Deliberately its own type behind <see cref="IScheduleDeliveryActions"/> rather than a method on
///     <see cref="ScheduleDiagnostics"/> — see that interface's remarks for why a read-only seam must stay
///     read-only.
/// </summary>
/// <remarks>
///     <para>
///     <b>Scoped, for the same reason as <see cref="ScheduleDiagnostics"/>.</b> This type injects
///     <see cref="ApplicationDbContext"/> directly, so it must share the scope's connection identity with
///     anything else that touches the outbox in the same request.
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
///     <b><see cref="IOutboxDashboardStore"/> is the intended seam</b> the comment in
///     <see cref="ScheduleDiagnostics"/> pointed at: it cannot say WHY a message died (no
///     <c>DeadLetterError</c>), only WHICH one and how to act on it, which is exactly the half this type needs.
///     </para>
/// </remarks>
/// <param name="db">The scope's context; see the remarks on why it is not a factory.</param>
/// <param name="dashboardStore">Requeues the resolved outbox row once it is found.</param>
/// <param name="serializer">The same serializer the host registers, so no wire format is assumed here.</param>
/// <param name="options">Reuses <see cref="ScheduleDiagnosticsOptions"/>'s dead-letter scan bounds.</param>
/// <param name="logger">Records a skipped, unreadable candidate row.</param>
public sealed class ScheduleDeliveryActions(
    ApplicationDbContext db,
    IOutboxDashboardStore dashboardStore,
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
        var since = _options.DeadLetterLookback > TimeSpan.Zero
            ? DateTimeOffset.UtcNow - _options.DeadLetterLookback
            : DateTimeOffset.UnixEpoch;

        var candidates = await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.TypeName == _channelMessageTypeName
                        && m.Status == OutboxMessageStatus.DeadLetter
                        && m.CreatedAt >= since)
            .OrderByDescending(m => m.CreatedAt)
            .Take(_options.DeadLetterScanLimit)
            .Select(m => new { m.Id, m.Payload })
            .ToListAsync(ct);

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
                await dashboardStore.RequeueAsync(candidate.Id, ct);
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
}
