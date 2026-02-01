using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Daedalus.Web;

/// <summary>
///     An <see cref="AuthenticationStateProvider" /> that always returns an authenticated user.
///     Used only when <c>TestMode=true</c> is set in configuration (E2E browser testing).
///     The authenticationType parameter ensures <see cref="ClaimsIdentity.IsAuthenticated" />
///     returns <c>true</c>, which makes <c>AuthorizeRouteView</c> render pages instead of
///     redirecting to the OIDC login flow.
/// </summary>
public sealed class AlwaysAuthenticatedStateProvider : AuthenticationStateProvider
{
    private static readonly Task<AuthenticationState> CachedState = Task.FromResult(
        new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "e2e-test-user"),
                new Claim(ClaimTypes.Name, "E2E Test User"),
                new Claim(ClaimTypes.Email, "e2e@daedalus.test"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim(ClaimTypes.Role, "task-manager"),
                new Claim(ClaimTypes.Role, "project-manager"),
                new Claim(ClaimTypes.Role, "analyst"),
            ],
            "TestAuth"))));

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => CachedState;
}
