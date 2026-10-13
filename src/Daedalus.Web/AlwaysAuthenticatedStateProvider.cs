using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Daedalus.Web;

/// <summary>
///     An <see cref="AuthenticationStateProvider" /> that always returns an authenticated user.
///     Used only when <c>TestMode=true</c> is set in configuration (E2E browser testing).
///     The authenticationType parameter ensures <see cref="ClaimsIdentity.IsAuthenticated" />
///     returns <c>true</c>, which makes <c>AuthorizeRouteView</c> render pages instead of
///     redirecting to the OIDC login flow.
/// </summary>
/// <remarks>
///     The user's roles are <see cref="DefaultRoles" /> unless the browser's <c>localStorage</c> holds a comma-separated
///     list under <see cref="RolesKey" />, which lets a browser test sign in as a narrower user in a context of its own.
///     The identity claims never change. This provider is registered only in test mode, so nothing else reads the key.
/// </remarks>
public sealed class AlwaysAuthenticatedStateProvider(IJSRuntime js) : AuthenticationStateProvider
{
    /// <summary>The <c>localStorage</c> key a test sets to choose the user's roles.</summary>
    public const string RolesKey = "daedalus.test-roles";

    private static readonly string[] DefaultRoles = ["admin", "task-manager", "project-manager", "analyst"];

    private Task<AuthenticationState>? _state;

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state ??= CreateAsync();

    private async Task<AuthenticationState> CreateAsync()
    {
        var stored = await js.InvokeAsync<string?>("localStorage.getItem", RolesKey);
        var roles = string.IsNullOrWhiteSpace(stored)
            ? DefaultRoles
            : stored.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, "e2e-test-user"),
            new(ClaimTypes.Name, "E2E Test User"),
            new(ClaimTypes.Email, "e2e@daedalus.test"),
            .. roles.Select(role => new Claim(ClaimTypes.Role, role)),
        ];

        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")));
    }
}
