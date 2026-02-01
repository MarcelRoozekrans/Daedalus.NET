namespace Daedalus.Tests.Playwright.Api.Utilities;

/// <summary>
///     Utility functions for API E2E tests (no Playwright dependencies).
/// </summary>
public static class TestUtilities
{
    /// <summary>
    ///     Waits for a condition to be true with retry logic.
    /// </summary>
    public static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        int timeoutMs = 10000,
        int intervalMs = 100)
    {
        var endTime = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < endTime)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(intervalMs).ConfigureAwait(false);
        }

        throw new TimeoutException($"Condition was not met within {timeoutMs}ms");
    }

    /// <summary>
    ///     Retries an action with exponential backoff.
    /// </summary>
    public static async Task RetryAsync(
        Func<Task> action,
        int maxAttempts = 3,
        int initialDelayMs = 100)
    {
        var attempt = 0;
        var delay = initialDelayMs;

        while (attempt < maxAttempts)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch when (attempt < maxAttempts - 1)
            {
                attempt++;
                await Task.Delay(delay).ConfigureAwait(false);
                delay *= 2;
            }
        }

        throw new InvalidOperationException($"Action failed after {maxAttempts} attempts");
    }

    /// <summary>
    ///     Retries a function that returns a value.
    /// </summary>
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> function,
        int maxAttempts = 3,
        int initialDelayMs = 100)
    {
        var attempt = 0;
        var delay = initialDelayMs;
        Exception? lastException = null;

        while (attempt < maxAttempts)
        {
            try
            {
                return await function().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts - 1)
            {
                attempt++;
                lastException = ex;
                await Task.Delay(delay).ConfigureAwait(false);
                delay *= 2;
            }
        }

        throw lastException ?? new InvalidOperationException($"Function failed after {maxAttempts} attempts");
    }

    /// <summary>
    ///     Generates a unique identifier for test data.
    /// </summary>
    public static string GenerateUniqueId(string prefix = "test") =>
        $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..32];

    /// <summary>
    ///     Creates a test data object with unique values.
    /// </summary>
    public static Dictionary<string, string> CreateTestData(params (string key, string value)[] values)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            data[key] = value;
        }

        return data;
    }
}
