using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daedalus.Tests.Playwright.Api.Fixtures;

/// <summary>
///     A test authentication handler that automatically authenticates all requests.
///     Used by E2E tests to bypass the real OIDC / JWT Bearer authentication.
/// </summary>
internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestScheme";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Grant every role used by the API's authorization policies (see Program.cs AddAuthorization).
        // Without these, endpoints behind role-based policies -- Admin, TaskManagement,
        // ProjectManagement, CodeAnalysis -- return 403 Forbidden for every E2E test, since an
        // authenticated principal with no role claims still fails RequireRole(...) checks.
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "e2e-test-user-id"), new Claim(ClaimTypes.Name, "E2E Test User"),
            new Claim(ClaimTypes.Email, "e2e@daedalus.test"), new Claim(ClaimTypes.Role, "admin"),
            new Claim(ClaimTypes.Role, "task-manager"), new Claim(ClaimTypes.Role, "project-manager"),
            new Claim(ClaimTypes.Role, "analyst")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
