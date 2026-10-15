using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.JSInterop;

namespace Daedalus.Web;

/// <summary>
///     Whether the console runs in test mode, from the <c>TestMode</c> configuration value, as Program.cs decides it once.
///     Components that behave differently in test mode inject this instead of reading configuration themselves.
/// </summary>
/// <param name="Enabled">Whether <c>TestMode</c> is <c>true</c>.</param>
public sealed record TestModeFlag(bool Enabled);

/// <summary>
///     How a test-mode console signs in, from the <c>TestAuth</c> configuration value. Read only when <c>TestMode</c> is
///     on (E2E browser testing); the OIDC build never reads it.
/// </summary>
public enum TestAuthMode
{
    /// <summary>The default: <see cref="AlwaysAuthenticatedStateProvider"/> and a plain HTTP client.</summary>
    SignedIn,

    /// <summary>No one is signed in: <see cref="SignedOutStateProvider"/>.</summary>
    SignedOut,

    /// <summary>
    ///     Signed in, with API requests sent through <see cref="AuthorizationMessageHandler"/> and
    ///     <see cref="TestAccessTokenProvider"/>, so a test can make a token refresh fail.
    /// </summary>
    Token,

    /// <summary>
    ///     Signed in as <see cref="AlwaysAuthenticatedStateProvider"/> signs in, but only once the test lets the
    ///     authentication state resolve: <see cref="LateAuthenticatedStateProvider"/>.
    /// </summary>
    Late,
}

/// <summary>Reads <see cref="TestAuthMode"/> from configuration.</summary>
public static class TestAuthModes
{
    /// <summary>The mode <paramref name="value"/> names; anything else is <see cref="TestAuthMode.SignedIn"/>.</summary>
    public static TestAuthMode Parse(string? value) => value?.Trim() switch
    {
        { } v when string.Equals(v, "signed-out", StringComparison.OrdinalIgnoreCase) => TestAuthMode.SignedOut,
        { } v when string.Equals(v, "token", StringComparison.OrdinalIgnoreCase) => TestAuthMode.Token,
        { } v when string.Equals(v, "late", StringComparison.OrdinalIgnoreCase) => TestAuthMode.Late,
        _ => TestAuthMode.SignedIn,
    };
}

/// <summary>Test mode only: no one is ever signed in.</summary>
public sealed class SignedOutStateProvider : AuthenticationStateProvider
{
    private static readonly Task<AuthenticationState> Anonymous =
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Anonymous;
}

/// <summary>
///     Test mode only: the signed-in test user, with the default roles, once the page's
///     <c>window.daedalusTestAuthGate()</c> promise resolves. A browser test defines the gate before the console starts
///     and resolves it when it has seen what the console shows while the sign-in is still being read, as an OIDC boot
///     reads its stored user.
/// </summary>
public sealed class LateAuthenticatedStateProvider(IJSRuntime js) : AuthenticationStateProvider
{
    /// <summary>The page function whose promise holds the authentication state back.</summary>
    public const string Gate = "daedalusTestAuthGate";

    private Task<AuthenticationState>? _state;

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state ??= ReadAsync();

    private async Task<AuthenticationState> ReadAsync()
    {
        await js.InvokeVoidAsync(Gate);
        return new AuthenticationState(AlwaysAuthenticatedStateProvider.User(AlwaysAuthenticatedStateProvider.DefaultRoles));
    }
}

/// <summary>
///     Test mode only: a token provider that answers as the OIDC one does. It hands out a fixed token, unless the
///     browser's <c>localStorage</c> holds <c>expired</c> under <see cref="ExpiredKey"/>; then it answers as a silent
///     refresh against a dead session does, with <see cref="AccessTokenResultStatus.RequiresRedirect"/>, the sign-in
///     path and a return to the current page.
/// </summary>
public sealed class TestAccessTokenProvider(IJSRuntime js, NavigationManager navigation) : IAccessTokenProvider
{
    /// <summary>The <c>localStorage</c> key a test sets to <c>expired</c> to fail every later token request.</summary>
    public const string ExpiredKey = "daedalus.test-token";

    /// <summary>The sign-in path, as the OIDC build's default authentication paths name it.</summary>
    private const string LogInPath = "authentication/login";

    /// <inheritdoc />
    public ValueTask<AccessTokenResult> RequestAccessToken() => RequestAsync();

    /// <inheritdoc />
    public ValueTask<AccessTokenResult> RequestAccessToken(AccessTokenRequestOptions options) => RequestAsync();

    private async ValueTask<AccessTokenResult> RequestAsync()
    {
        var stored = await js.InvokeAsync<string?>("localStorage.getItem", ExpiredKey);
        if (string.Equals(stored, "expired", StringComparison.Ordinal))
        {
            return new AccessTokenResult(
                AccessTokenResultStatus.RequiresRedirect,
                token: null!,
                LogInPath,
                new InteractiveRequestOptions { Interaction = InteractionType.GetToken, ReturnUrl = navigation.Uri });
        }

        // Already expiring: AuthorizationMessageHandler keeps a token until five minutes before it expires, and this one
        // is past that, so every request asks again and a test can fail the next one.
        var token = new AccessToken { Value = "e2e-test-token", Expires = DateTimeOffset.UtcNow, GrantedScopes = [] };
        return new AccessTokenResult(AccessTokenResultStatus.Success, token, interactiveRequestUrl: null!, interactiveRequest: null!);
    }
}
