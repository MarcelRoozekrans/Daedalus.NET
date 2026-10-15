namespace Daedalus.Tests.Playwright.Browser.Utilities;

/// <summary>
///     Chooses how the console signs in for one page, through the test-mode configuration it boots with. The fixture
///     serves <c>{"TestMode":"true"}</c>, which signs every page in; a test that needs another state routes the
///     configuration for its own page, so the rest of the suite keeps the default.
/// </summary>
public static class TestAuth
{
    /// <summary>No one is signed in: every authentication state is anonymous.</summary>
    public const string SignedOut = "signed-out";

    /// <summary>
    ///     Signed in, with requests to the API carrying a bearer token from a test token provider. The provider reads
    ///     <see cref="TokenKey"/> on every request, and <c>expired</c> there makes it answer as a token refresh that
    ///     failed.
    /// </summary>
    public const string Token = "token";

    /// <summary>The <c>localStorage</c> key the test token provider reads; <c>expired</c> fails every token request.</summary>
    public const string TokenKey = "daedalus.test-token";

    /// <summary>
    ///     The OIDC client's stored user, under the key it writes in <c>sessionStorage</c>. A test seeds it to see that an
    ///     expired session clears it.
    /// </summary>
    public const string StoredUserKey = "oidc.user:http://keycloak.test/realms/daedalus:daedalus-wasm";

    /// <summary>Boots the console on <paramref name="page"/> in <paramref name="mode"/>.</summary>
    public static Task UseAsync(IPage page, string mode) =>
        page.RouteAsync("**/appsettings*.json", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Cache-Control"] = "no-store" },
            Body = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TestMode"] = "true",
                ["TestAuth"] = mode,
            }),
        }));

    /// <summary>Makes every later token request on <paramref name="page"/> fail, as a refresh against a dead session does.</summary>
    public static Task ExpireTokenAsync(IPage page) =>
        page.EvaluateAsync("([key]) => localStorage.setItem(key, 'expired')", new[] { TokenKey });

    /// <summary>Makes every token request fail from the first one, before the console starts.</summary>
    public static Task ExpireTokenFromStartAsync(IPage page) =>
        page.AddInitScriptAsync($"localStorage.setItem('{TokenKey}', 'expired');");

    /// <summary>Puts an OIDC user in <c>sessionStorage</c> before the console starts, once per page.</summary>
    public static Task SeedStoredUserAsync(IPage page) =>
        page.AddInitScriptAsync(
            $"if (!sessionStorage.getItem('daedalus.test-seeded')) {{ sessionStorage.setItem('daedalus.test-seeded', '1'); " +
            $"sessionStorage.setItem('{StoredUserKey}', '{{\"profile\":{{\"name\":\"E2E Test User\"}}}}'); }}");

    /// <summary>Whether the seeded OIDC user is still in <c>sessionStorage</c>.</summary>
    public static Task<bool> HasStoredUserAsync(IPage page) =>
        page.EvaluateAsync<bool>("([key]) => sessionStorage.getItem(key) !== null", new[] { StoredUserKey });

    /// <summary>
    ///     Records every path whose document ever contained <paramref name="text"/>, however briefly, so a test sees an
    ///     alert that showed and went before it looked. Read it with <see cref="PathsThatShowedAsync"/>.
    /// </summary>
    public static Task WatchForTextAsync(IPage page, string text) =>
        page.AddInitScriptAsync(
            "window.__daedalusShowed = []; " +
            $"const needle = {JsonSerializer.Serialize(text)}; " +
            "const look = () => { const t = document.documentElement ? document.documentElement.textContent : ''; " +
            "if (t && t.includes(needle) && !window.__daedalusShowed.includes(location.pathname)) " +
            "{ window.__daedalusShowed.push(location.pathname); } }; " +
            "new MutationObserver(look).observe(document, { subtree: true, childList: true, characterData: true });");

    /// <summary>The paths recorded by <see cref="WatchForTextAsync"/>.</summary>
    public static Task<string[]> PathsThatShowedAsync(IPage page) =>
        page.EvaluateAsync<string[]>("() => window.__daedalusShowed || []");

    /// <summary>
    ///     The return URL of the sign-in the console navigated to. Blazor's <c>NavigateToLogin</c> keeps it in the history
    ///     entry's state, not in the address, so it is read from <c>history.state</c>; null when there is none.
    /// </summary>
    public static async Task<string?> ReturnUrlAsync(IPage page)
    {
        var userState = await page.EvaluateAsync<string?>(
            "() => history.state && typeof history.state.userState === 'string' ? history.state.userState : null")
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(userState))
        {
            return null;
        }

        using var document = JsonDocument.Parse(userState);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "returnUrl", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}
