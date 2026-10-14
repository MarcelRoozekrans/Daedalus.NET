using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Daedalus.Agents.Decisions;

/// <summary>
///     Tells once, when the host starts, whether per-node decisions are on and, when they are not, why. Names the
///     reason only: never the OpenRouter key, nor its length.
/// </summary>
/// <param name="config">The validated decision settings.</param>
/// <param name="keyPresent">Whether an OpenRouter key was configured. The key itself is not held.</param>
/// <param name="logger">The logger.</param>
public sealed partial class DecisionsStartupLog(DecisionsConfig config, bool keyPresent, ILogger<DecisionsStartupLog> logger) : IHostedService
{
    /// <summary>The reason when the owner has not confirmed a zero-data-retention OpenRouter account.</summary>
    public const string NoZeroRetentionConfirmation = "Thalos:Decisions:ZeroDataRetentionAccount is not true";

    /// <summary>The reason when no OpenRouter key is configured.</summary>
    public const string NoKey = "no OpenRouter key is configured";

    /// <summary>The reason when neither the confirmation nor a key is present.</summary>
    public const string NoConfirmationAndNoKey = "Thalos:Decisions:ZeroDataRetentionAccount is not true and no OpenRouter key is configured";

    /// <summary>Why decisions are off, or <see langword="null"/> when they are on.</summary>
    public static string? WhyOff(DecisionsConfig config, bool keyPresent)
    {
        ArgumentNullException.ThrowIfNull(config);
        return (config.ZeroDataRetentionAccount, keyPresent) switch
        {
            (true, true) => null,
            (false, true) => NoZeroRetentionConfirmation,
            (true, false) => NoKey,
            (false, false) => NoConfirmationAndNoKey,
        };
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (WhyOff(config, keyPresent) is { } reason)
        {
            LogOff(logger, reason);
        }
        else
        {
            LogOn(logger, config.Model);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Per-node model and tool decisions are off: {Reason}. Every node runs on its default model and full tool list")]
    private static partial void LogOff(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Per-node model and tool decisions are on, decided by {Model} through OpenRouter")]
    private static partial void LogOn(ILogger logger, string model);
}
