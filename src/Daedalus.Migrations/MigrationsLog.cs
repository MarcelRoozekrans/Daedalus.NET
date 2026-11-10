using Microsoft.Extensions.Logging;

namespace Daedalus.Migrations;

internal static partial class MigrationsLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Workflow engine migration: {UpgradedHistoryRows} pre-scoping history rows attributed to their source, {AppliedCount} migrations applied")]
    public static partial void WorkflowEngineMigrationApplied(this ILogger logger, int upgradedHistoryRows, int appliedCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Daedalus migration: {AppliedCount} migrations applied")]
    public static partial void DaedalusMigrationApplied(this ILogger logger, int appliedCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Daedalus migration refused: {Reason}")]
    public static partial void DaedalusMigrationRefused(this ILogger logger, string reason);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Migration history upgrade: {UpgradedHistoryRows} pre-scoping rows attributed to their source")]
    public static partial void MigrationHistoryUpgraded(this ILogger logger, int upgradedHistoryRows);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error,
        Message = "Migration history upgrade refused: {Reason}")]
    public static partial void MigrationHistoryUpgradeRefused(this ILogger logger, string reason);
}
