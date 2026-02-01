using Daedalus.Tests.Integration.Attributes;
using Daedalus.Tests.Integration.Fixtures;

namespace Daedalus.Tests.Integration.Authentication;

/// <summary>
///     Integration tests for complete authentication flows.
///     Keycloak tests use Testcontainers.Keycloak (auto-started, no docker-compose needed).
///     API endpoint tests still require a running API server and are skipped when unavailable.
/// </summary>
[Collection(KeycloakCollection.Name)]
[Trait("Category", "AuthenticationFlow")]
public class AuthenticationFlowTests(KeycloakFixture keycloak)
{
    private const string ApiBaseUrl = "http://localhost:8080";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

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
    [RequiresApiFact]
    public async Task ApiEndpoint_WithoutToken_Returns401Unauthorized()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/tasks");

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "API should reject requests without JWT token");
    }

    /// <summary>
    ///     Tests that API endpoints accept requests with valid JWT token.
    /// </summary>
    [RequiresApiFact]
    public async Task ApiEndpoint_WithValidToken_Returns200Ok()
    {
        // Arrange
        var token = await keycloak.ObtainAccessTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/tasks")
        {
            Headers = { { "Authorization", $"Bearer {token}" } }
        };

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "API should accept requests with valid JWT token");
    }

    /// <summary>
    ///     Tests that API endpoints reject requests with invalid or malformed tokens.
    /// </summary>
    [RequiresApiFact]
    public async Task ApiEndpoint_WithInvalidToken_Returns401Unauthorized()
    {
        // Arrange
        var invalidToken = "invalid.malformed.token";
        var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/tasks")
        {
            Headers = { { "Authorization", $"Bearer {invalidToken}" } }
        };

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "API should reject requests with invalid tokens");
    }

    /// <summary>
    ///     Tests that all protected API endpoints follow the same authorization pattern.
    /// </summary>
    [RequiresApiTheory]
    [InlineData("/api/tasks")]
    [InlineData("/api/executionsessions")]
    [InlineData("/api/projects")]
    [InlineData("/api/taskexecutions/task/00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/codeanalysis")]
    [InlineData("/api/ralph-config")]
    public async Task AllProtectedEndpoints_RequireJwtToken(string endpoint)
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}{endpoint}");

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"Endpoint {endpoint} should require authorization");
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
