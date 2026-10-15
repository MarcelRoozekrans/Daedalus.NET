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

    /// <summary>Signed in, but the authentication state waits until <see cref="ReleaseSignInAsync"/>.</summary>
    public const string Late = "late";

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
    ///     Records every path whose document ever held an element matching <paramref name="selector"/>, and holding
    ///     <paramref name="text"/> when one is given, however briefly: an alert or a notification that showed and went
    ///     before the test looked is still recorded. Read it with <see cref="PathsThatShowedAsync"/> and the same
    ///     arguments; several watches on one page keep separate records.
    /// </summary>
    public static Task WatchForAsync(IPage page, string selector, string? text = null) =>
        page.AddInitScriptAsync(
            "(() => { window.__daedalusShowed = window.__daedalusShowed || {}; " +
            $"const key = {JsonSerializer.Serialize(WatchKey(selector, text))}; " +
            $"const selector = {JsonSerializer.Serialize(selector)}; const text = {JsonSerializer.Serialize(text)}; " +
            "const seen = window.__daedalusShowed[key] = []; " +
            "const look = () => { const hit = Array.from(document.querySelectorAll(selector))" +
            ".some(e => text === null || (e.textContent || '').includes(text)); " +
            "if (hit && !seen.includes(location.pathname)) { seen.push(location.pathname); } }; " +
            "new MutationObserver(look).observe(document, { subtree: true, childList: true, attributes: true, characterData: true }); })();");

    /// <summary>
    ///     Makes the console's authentication state wait for <see cref="ReleaseSignInAsync"/>, in the <c>late</c> mode,
    ///     as an OIDC boot waits while it reads the stored user.
    /// </summary>
    public static Task HoldSignInAsync(IPage page) =>
        page.AddInitScriptAsync(
            "const held = new Promise(resolve => { window.daedalusReleaseAuth = resolve; }); " +
            "window.daedalusTestAuthGate = () => held;");

    /// <summary>Lets the held authentication state resolve.</summary>
    public static Task ReleaseSignInAsync(IPage page) => page.EvaluateAsync("() => window.daedalusReleaseAuth()");

    /// <summary>The paths recorded by <see cref="WatchForAsync"/> for the same <paramref name="selector"/> and <paramref name="text"/>.</summary>
    public static Task<string[]> PathsThatShowedAsync(IPage page, string selector, string? text = null) =>
        page.EvaluateAsync<string[]>(
            "key => (window.__daedalusShowed && window.__daedalusShowed[key]) || []", WatchKey(selector, text));

    private static string WatchKey(string selector, string? text) => text is null ? selector : $"{selector}|{text}";

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
