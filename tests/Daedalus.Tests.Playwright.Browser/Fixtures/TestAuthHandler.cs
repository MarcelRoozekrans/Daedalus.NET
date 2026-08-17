using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daedalus.Tests.Playwright.Browser.Fixtures;

internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestScheme";

    /// <summary>Subject of every E2E request; also the owner of the seeded memories and sessions.</summary>
    public const string UserId = "e2e-test-user-id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, UserId),
            new Claim(ClaimTypes.Name, "E2E Test User"),
            new Claim(ClaimTypes.Email, "e2e@daedalus.test"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim(ClaimTypes.Role, "task-manager"),
            new Claim(ClaimTypes.Role, "project-manager"),
            new Claim(ClaimTypes.Role, "analyst"),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
