using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Daedalus.Agents.Security;
using Daedalus.Tests.Integration.Fixtures;
using Thalos;
using ZeroAlloc.Authorization;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Authentication;

/// <summary>
///     Regression guard for a live defect: against real Keycloak, <c>GET /api/agents</c> answered 200 while every
///     <c>AgentSessionsController</c> endpoint answered 401 because both realm clients declared
///     <c>defaultClientScopes: ["profile", "email"]</c> and omitted Keycloak's built-in <c>basic</c> scope — where
///     <c>sub</c> lives in Keycloak 24+. With no <c>sub</c>, <see cref="ClaimsSecurityContext.Id"/> fell through to
///     <see cref="AnonymousSecurityContext.AnonymousId"/>. It survived phases 1.1–1.3 because
///     <see cref="Daedalus.Tests.Integration.Api.AgentEndpointsSmokeTests"/> substitutes <see cref="HeaderTestAuthHandler"/>, which fabricates a
///     <c>sub</c> claim directly — no test in this repository has ever exercised a real Keycloak claim shape. This test
///     mints a token from the real Testcontainers Keycloak instance and runs it through the production JWT Bearer
///     pipeline (see <see cref="ApiWebApplicationFactory"/>'s <c>keycloak</c> parameter), never
///     <see cref="HeaderTestAuthHandler"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RealKeycloakIdentityTests(PostgresFixture postgres, KeycloakFixture keycloak)
    : IClassFixture<KeycloakFixture>, IAsyncLifetime
{
    private readonly IAgentRuntime _runtime = Substitute.For<IAgentRuntime>();
    private ApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await postgres.DatabaseResetter.ResetAsync();
        _factory = new ApiWebApplicationFactory(postgres.ConnectionString, _runtime, keycloak);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Real_keycloak_token_carries_sub_and_is_accepted_by_a_session_scoped_endpoint()
    {
        // A real user login (Resource Owner Password Flow against the dev/dev123 user), exactly as
        // AuthenticationFlowTests obtains tokens — never HeaderTestAuthHandler's fabricated claims.
        var token = await keycloak.ObtainUserAccessTokenAsync();

        // Assertion 1 — the load-bearing one. Decode the real token straight from the real container and assert `sub`
        // is present and non-empty. A regression in keycloak-realm.json's defaultClientScopes (dropping `basic`) must
        // fail HERE, with a clear message, rather than downstream as a mystery 401.
        var payloadJson = KeycloakFixture.DecodeBase64Url(token.Split('.')[1]);
        using var payload = JsonDocument.Parse(payloadJson);
        payload.RootElement.TryGetProperty("sub", out var subElement).Should().BeTrue(
            "the access token must carry a top-level `sub` claim; its absence means keycloak-realm.json's " +
            "defaultClientScopes is missing Keycloak's built-in `basic` scope, where `sub` lives in Keycloak 24+");
        var sub = subElement.GetString();
        sub.Should().NotBeNullOrWhiteSpace("`sub` must be a real, non-empty subject id");

        // Assertion 2 — ClaimsSecurityContext built from the real `sub` resolves to that subject, not anonymous. This
        // mirrors exactly what HttpSecurityContextFactory.TryCreate does with the principal the JWT Bearer handler
        // produces (raw `sub`, unmapped — ASP.NET Core's JsonWebTokenHandler defaults MapInboundClaims to false).
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub!)], "Bearer"));
        var context = new ClaimsSecurityContext(principal);
        context.Id.Should().Be(sub);
        context.Id.Should().NotBe(AnonymousSecurityContext.AnonymousId);

        // Assertion 3 — the production JWT Bearer pipeline (not HeaderTestAuthHandler) validates the real token
        // end-to-end and lets an authenticated caller reach a session-scoped endpoint.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/agents/sessions", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, await response.Content.ReadAsStringAsync());
    }
}
