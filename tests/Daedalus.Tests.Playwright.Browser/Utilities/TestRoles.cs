namespace Daedalus.Tests.Playwright.Browser.Utilities;

/// <summary>
///     Chooses the test-mode user's roles in a browser context, through the <c>localStorage</c> key the console's
///     test-mode authentication provider reads.
/// </summary>
public static class TestRoles
{
    /// <summary>The key <c>AlwaysAuthenticatedStateProvider</c> reads the roles from.</summary>
    public const string Key = "daedalus.test-roles";

    /// <summary>
    ///     Writes <paramref name="roles"/>, comma-separated, from a page of its own on the same origin. A page of the
    ///     console already open in <paramref name="context"/> receives the <c>storage</c> event and announces the new
    ///     authentication state without a reload, as a sign-in that completes after the console rendered; a page
    ///     opened later reads the roles as it starts.
    /// </summary>
    public static async Task WriteAsync(IBrowserContext context, Uri baseUrl, string roles)
    {
        var writer = await context.NewPageAsync().ConfigureAwait(false);
        try
        {
            await writer.GotoAsync(new Uri(baseUrl, "/favicon.svg").ToString()).ConfigureAwait(false);
            await writer.EvaluateAsync("([key, value]) => localStorage.setItem(key, value)", new[] { Key, roles })
                .ConfigureAwait(false);
        }
        finally
        {
            await writer.CloseAsync().ConfigureAwait(false);
        }
    }
}
