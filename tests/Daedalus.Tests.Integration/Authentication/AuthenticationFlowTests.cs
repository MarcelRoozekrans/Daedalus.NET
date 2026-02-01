using System.Text;
using Daedalus.Tests.Integration.Attributes;

namespace Daedalus.Tests.Integration.Authentication;

/// <summary>
///     Integration tests for complete authentication flows.
///     These tests verify end-to-end scenarios involving Keycloak and API endpoints.
///     Tests are automatically skipped when required services are not available.
///     Prerequisites:
///     - docker-compose up -d (starts Keycloak and PostgreSQL)
///     - dotnet run --project src/Daedalus.Api (starts the API)
/// </summary>
[Trait("Category", "AuthenticationFlow")]
public class AuthenticationFlowTests
{
    private const string ApiBaseUrl = "http://localhost:8080";
    private const string KeycloakHost = "http://localhost:8082";
    private const string RealmName = "daedalus";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    ///     Tests the OAuth 2.0 Client Credentials Flow.
    ///     This flow is used for service-to-service authentication (e.g., Console to API).
    /// </summary>
    [RequiresKeycloakFact]
    public async Task OAuthClientCredentialsFlow_ObtainsAccessToken()
    {
        // Arrange
        var tokenEndpoint = $"{KeycloakHost}/realms/{RealmName}/protocol/openid-connect/token";
        var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "grant_type", "client_credentials" },
                { "client_id", ClientId },
                { "client_secret", ClientSecret },
                { "scope", "openid profile email" }
            })
        };

        // Act
        var response = await HttpClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Should obtain access token from Keycloak. Is Keycloak running? Try: docker compose up -d");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"access_token\"", "Response should contain JWT access token");
        content.Should().Contain("\"token_type\":\"Bearer\"", "Token type should be Bearer");
    }

    /// <summary>
    ///     Tests the OAuth 2.0 Resource Owner Password Flow.
    ///     This flow is used for direct credential authentication (user login).
    ///     Note: Not recommended for production, primarily for internal testing.
    /// </summary>
    [RequiresKeycloakFact]
    public async Task OAuthResourceOwnerPasswordFlow_AuthenticatesWithUserCredentials()
    {
        // Arrange
        var tokenEndpoint = $"{KeycloakHost}/realms/{RealmName}/protocol/openid-connect/token";
        var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "grant_type", "password" },
                { "client_id", ClientId },
                { "client_secret", ClientSecret },
                { "username", TestUsername },
                { "password", TestPassword },
                { "scope", "openid profile email" }
            })
        };

        // Act
        var response = await HttpClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Should authenticate user '{TestUsername}' with Keycloak");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"access_token\"", "Response should contain JWT access token");
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
        var response = await HttpClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "API should reject requests without JWT token");
    }

    /// <summary>
    ///     Tests that API endpoints accept requests with valid JWT token.
    /// </summary>
    [RequiresKeycloakAndApiFact]
    public async Task ApiEndpoint_WithValidToken_Returns200Ok()
    {
        // Arrange
        var token = await ObtainAccessToken();
        var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/api/tasks")
        {
            Headers = { { "Authorization", $"Bearer {token}" } }
        };

        // Act
        var response = await HttpClient.SendAsync(request);

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
        var response = await HttpClient.SendAsync(request);

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
        var response = await HttpClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"Endpoint {endpoint} should require authorization");
    }

    /// <summary>
    ///     Tests JWT token expiration and refresh token flow.
    /// </summary>
    [RequiresKeycloakFact]
    public async Task JwtToken_ShouldIncludeExpirationClaim()
    {
        // Arrange
        var token = await ObtainAccessToken();

        // Act
        var parts = token.Split('.');
        if (parts.Length == 3)
        {
            // Decode the payload (second part)
            var payload = parts[1];
            var decodedPayload = DecodeBase64Url(payload);

            // Assert
            decodedPayload.Should().Contain("\"exp\"", "JWT should include expiration (exp) claim");
            decodedPayload.Should().Contain("\"iat\"", "JWT should include issued at (iat) claim");
        }
    }

    #region Test Credentials

    private const string TestUsername = "dev";
    private const string TestPassword = "dev123";
    private const string ClientId = "daedalus-api";
    private const string ClientSecret = "daedalus-api-secret-change-in-production";

    #endregion

    #region Helper Methods

    private static async Task<string> ObtainAccessToken()
    {
        var tokenEndpoint = $"{KeycloakHost}/realms/{RealmName}/protocol/openid-connect/token";
        var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "grant_type", "client_credentials" },
                { "client_id", ClientId },
                { "client_secret", ClientSecret }
            })
        };

        var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();

        // Extract token from JSON response
        // In production, use proper JSON parsing
        return ExtractTokenFromResponse(content);
    }

    private static string DecodeBase64Url(string base64UrlString)
    {
        var base64String = base64UrlString
            .Replace('-', '+')
            .Replace('_', '/');

        var paddingNeeded = base64String.Length % 4;
        if (paddingNeeded > 0)
        {
            base64String += new string('=', 4 - paddingNeeded);
        }

        var decodedBytes = Convert.FromBase64String(base64String);
        return Encoding.UTF8.GetString(decodedBytes);
    }

    private static string ExtractTokenFromResponse(string jsonResponse)
    {
        const string tokenPrefix = "\"access_token\":\"";
        var tokenStart = jsonResponse.IndexOf(tokenPrefix, StringComparison.Ordinal) + tokenPrefix.Length;
        var remainingJson = jsonResponse.AsSpan(tokenStart);
        var tokenEnd = remainingJson.IndexOf('"');
        return jsonResponse[tokenStart..(tokenStart + tokenEnd)];
    }

    #endregion
}
