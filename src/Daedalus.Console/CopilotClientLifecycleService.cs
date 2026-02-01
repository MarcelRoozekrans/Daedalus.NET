using GitHub.Copilot.SDK;

namespace Daedalus.Console;

/// <summary>
///     Manages the lifecycle of the CopilotClient.
///     Ensures the client is properly started and stopped with the application.
/// </summary>
internal sealed partial class CopilotClientLifecycleService(
    CopilotClient client,
    ILogger<CopilotClientLifecycleService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            LogStartingCopilot(logger);
            await client.StartAsync().ConfigureAwait(false);
            LogCopilotStartedSuccessfully(logger);
        }
        catch (Exception ex)
        {
            LogFailedStartCopilot(logger, ex);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            LogStoppingCopilot(logger);

            if (client.State == ConnectionState.Connected)
            {
                await client.StopAsync().ConfigureAwait(false);
            }

            await client.DisposeAsync().ConfigureAwait(false);
            LogCopilotStoppedSuccessfully(logger);
        }
        catch (Exception ex)
        {
            LogErrorStoppingCopilot(logger, ex);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting Copilot client")]
    private static partial void LogStartingCopilot(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Copilot client started successfully")]
    private static partial void LogCopilotStartedSuccessfully(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to start Copilot client")]
    private static partial void LogFailedStartCopilot(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Stopping Copilot client")]
    private static partial void LogStoppingCopilot(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Copilot client stopped successfully")]
    private static partial void LogCopilotStoppedSuccessfully(ILogger logger);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Error stopping Copilot client")]
    private static partial void LogErrorStoppingCopilot(ILogger logger, Exception exception);
}
