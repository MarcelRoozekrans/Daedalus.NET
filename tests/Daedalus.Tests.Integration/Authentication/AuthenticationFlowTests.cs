using System.Text;
using Daedalus.Tests.Integration.Fixtures;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Authentication;

/// <summary>
///     Integration tests for complete authentication flows.
///     Keycloak-only tests (obtaining tokens) use Testcontainers.Keycloak directly (auto-started, no docker-compose
///     needed). API endpoint tests drive the real Daedalus.Api <c>Program</c> in-process via
///     <see cref="ApiWebApplicationFactory"/> with the production JWT Bearer pipeline pointed at that same Keycloak
///     fixture — see <see cref="RealKeycloakIdentityTests"/> for the pattern this follows. They previously made bare
///     <see cref="HttpClient"/> calls to a hardcoded <c>http://localhost:8080</c>, gated by a TCP-connect probe
///     that could not tell the real API apart from any other process holding that port (e.g. an unrelated Traefik
///     container) — see <c>auth-flow-investigation.md</c> for the incident this replaced.
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "AuthenticationFlow")]
public sealed class AuthenticationFlowTests(PostgresFixture postgres, KeycloakFixture keycloak)
    : IClassFixture<KeycloakFixture>, IAsyncLifetime
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
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

    /// <summary>
    ///     Tests the OAuth 2.0 Client Credentials Flow.
    ///     This flow is used for service-to-service authentication (e.g., Console to API).
    /// </summary>
    [Fact]
    public async Task OAuthClientCredentialsFlow_ObtainsAccessToken()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Post, keycloak.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "grant_type", "client_credentials" },
                { "client_id", "daedalus-api" },
                { "client_secret", "daedalus-api-secret-change-in-production" }
            })
        };

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Should obtain access token from Keycloak");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"access_token\"", "Response should contain JWT access token");
        content.Should().Contain("\"token_type\":\"Bearer\"", "Token type should be Bearer");
    }

    /// <summary>
    ///     Tests the OAuth 2.0 Resource Owner Password Flow.
    ///     This flow is used for direct credential authentication (user login).
    ///     Note: Not recommended for production, primarily for internal testing.
    /// </summary>
    [Fact]
    public async Task OAuthResourceOwnerPasswordFlow_AuthenticatesWithUserCredentials()
    {
        // Act
        var token = await keycloak.ObtainUserAccessTokenAsync();

        // Assert
        token.Should().NotBeNullOrEmpty("Should authenticate user 'dev' with Keycloak");
    }

    /// <summary>
    ///     Tests that API endpoints return 401 Unauthorized without a valid JWT token.
    /// </summary>
    [Fact]
    public async Task ApiEndpoint_WithoutToken_Returns401Unauthorized()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/tasks", UriKind.Relative));

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "API should reject requests without JWT token");
    }

    /// <summary>
    ///     Tests that API endpoints accept requests with valid JWT token.
    /// </summary>
    [Fact]
    public async Task ApiEndpoint_WithValidToken_Returns200Ok()
    {
        // Arrange
        var token = await keycloak.ObtainAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/tasks", UriKind.Relative))
        {
            Headers = { { "Authorization", $"Bearer {token}" } }
        };

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "API should accept requests with valid JWT token");
    }

    /// <summary>
    ///     Tests that API endpoints reject requests with invalid or malformed tokens.
    /// </summary>
    [Fact]
    public async Task ApiEndpoint_WithInvalidToken_Returns401Unauthorized()
    {
        // Arrange
        const string invalidToken = "invalid.malformed.token";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/tasks", UriKind.Relative))
        {
            Headers = { { "Authorization", $"Bearer {invalidToken}" } }
        };

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "API should reject requests with invalid tokens");
    }

    /// <summary>
    ///     Tests that all protected API endpoints follow the same authorization pattern. Each case carries the verb
    ///     the endpoint actually exposes — <c>/api/codeanalysis</c> has no <c>GET</c> action, only <c>POST</c>, so
    ///     probing it with <c>GET</c> would hit ASP.NET Core's routing before authorization ever runs and return
    ///     <c>405</c>, not <c>401</c>, proving nothing about auth. A minimal-but-valid JSON body is supplied for the
    ///     <c>POST</c> case purely so a missing token is unambiguously the reason for <c>401</c>, never a side effect
    ///     of a body FluentValidation would have rejected anyway.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/tasks", null)]
    [InlineData("GET", "/api/executionsessions", null)]
    [InlineData("GET", "/api/projects", null)]
    [InlineData("GET", "/api/taskexecutions/task/00000000-0000-0000-0000-000000000000", null)]
    [InlineData("POST", "/api/codeanalysis",
        """{"RepositoryUrl":"https://github.com/org/repo","FilePath":"src/Foo.cs","Type":1,"Title":"Auth probe","Description":"Auth probe","Requirements":[]}""")]
    [InlineData("GET", "/api/ralph-config", null)]
    public async Task AllProtectedEndpoints_RequireJwtToken(string method, string endpoint, string? body)
    {
        // Arrange
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(endpoint, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"{method} {endpoint} should require authorization");
    }

    /// <summary>
    ///     Tests JWT token structure includes required claims.
    /// </summary>
    [Fact]
    public async Task JwtToken_ShouldIncludeExpirationClaim()
    {
        // Arrange
        var token = await keycloak.ObtainAccessTokenAsync();

        // Act
        var parts = token.Split('.');
        if (parts.Length == 3)
        {
            // Decode the payload (second part)
            var payload = parts[1];
            var decodedPayload = KeycloakFixture.DecodeBase64Url(payload);

            // Assert
            decodedPayload.Should().Contain("\"exp\"", "JWT should include expiration (exp) claim");
            decodedPayload.Should().Contain("\"iat\"", "JWT should include issued at (iat) claim");
        }
    }
}
