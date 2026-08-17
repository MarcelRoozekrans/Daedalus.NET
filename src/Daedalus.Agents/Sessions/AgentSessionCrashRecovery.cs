using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Agents.Sessions;

/// <summary>
///     Startup crash recovery: a host that died mid-turn leaves sessions stuck in <see cref="AgentSessionState.Running"/>,
///     which the runtime would answer with <c>SessionBusy</c> forever. On start every <c>Running</c> session is reset to
///     <see cref="AgentSessionState.Idle"/> (one atomic <c>UPDATE</c>). Runs before Kestrel accepts requests; multi-instance
///     deployments would need a lease instead — Daedalus runs a single API instance.
/// </summary>
/// <remarks>An unreachable database is logged and skipped (never fails host start) so test hosts without a DB still boot.</remarks>
internal sealed partial class AgentSessionCrashRecovery(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    TimeProvider clock,
    ILogger<AgentSessionCrashRecovery> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow().UtcDateTime;

            var reset = await db.AgentSessions
                .Where(s => s.State == AgentSessionState.Running)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.State, AgentSessionState.Idle)
                    .SetProperty(s => s.LastActivityAt, now), cancellationToken)
                .ConfigureAwait(false);

            if (reset > 0)
            {
                LogReset(logger, reset);
            }
            else
            {
                LogNothingToReset(logger);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSkipped(logger, ex.GetType().Name, ex.Message);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 400, Level = LogLevel.Warning, Message = "Agent session crash recovery reset {Count} stale Running session(s) to Idle")]
    private static partial void LogReset(ILogger logger, int count);

    [LoggerMessage(EventId = 401, Level = LogLevel.Debug, Message = "Agent session crash recovery: no stale Running sessions")]
    private static partial void LogNothingToReset(ILogger logger);

    [LoggerMessage(EventId = 402, Level = LogLevel.Warning, Message = "Agent session crash recovery skipped: database unavailable ({ExceptionType}: {Reason})")]
    private static partial void LogSkipped(ILogger logger, string exceptionType, string reason);
}
