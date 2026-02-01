using System.Text.RegularExpressions;

namespace Daedalus.Tests.Playwright.Browser.Utilities;

/// <summary>
///     Utility functions for E2E tests (includes Playwright helpers).
/// </summary>
public static class TestUtilities
{
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

    public static string GenerateUniqueId(string prefix = "test") =>
        $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..32];

    public static Dictionary<string, string> CreateTestData(params (string key, string value)[] values)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            data[key] = value;
        }

        return data;
    }

    /// <summary>
    ///     Waits for a locator to contain specific text.
    /// </summary>
    public static async Task WaitForTextAsync(
        ILocator locator,
        string text,
        int timeoutMs = 5000)
    {
        var endTime = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < endTime)
        {
            try
            {
                var content = await locator.TextContentAsync().ConfigureAwait(false);
                if (content?.Contains(text, StringComparison.Ordinal) == true)
                {
                    return;
                }
            }
            catch
            {
                // Intentionally swallowed — retry until timeout
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"Text '{text}' was not found within {timeoutMs}ms");
    }

    /// <summary>
    ///     Extracts a value from page content using a regex pattern.
    /// </summary>
    public static async Task<string?> ExtractValueAsync(IPage page, string pattern)
    {
        var content = await page.ContentAsync().ConfigureAwait(false);
        var match = Regex.Match(content, pattern, RegexOptions.None, TimeSpan.FromSeconds(5));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    ///     Gets all text values from multiple elements.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetAllTextAsync(ILocator locator)
    {
        var count = await locator.CountAsync().ConfigureAwait(false);
        var texts = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var text = await locator.Nth(i).TextContentAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(text))
            {
                texts.Add(text);
            }
        }

        return texts;
    }
}
