using Microsoft.Extensions.Logging;

namespace Daedalus.Migrations;

internal static partial class MigrationsLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Workflow engine migration: {UpgradedHistoryRows} pre-scoping history rows attributed to their source, {AppliedCount} migrations applied")]
    public static partial void WorkflowEngineMigrationApplied(this ILogger logger, int upgradedHistoryRows, int appliedCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "Workflow engine migration refused: {Reason}")]
    public static partial void WorkflowEngineMigrationRefused(this ILogger logger, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Daedalus migration: {AppliedCount} migrations applied")]
    public static partial void DaedalusMigrationApplied(this ILogger logger, int appliedCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Daedalus migration refused: {Reason}")]
    public static partial void DaedalusMigrationRefused(this ILogger logger, string reason);
}
