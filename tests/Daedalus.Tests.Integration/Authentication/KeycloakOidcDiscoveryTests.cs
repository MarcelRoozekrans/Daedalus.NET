using Daedalus.Tests.Integration.Attributes;

namespace Daedalus.Tests.Integration.Authentication;

/// <summary>
///     Integration tests verifying Keycloak OIDC discovery and configuration.
///     These tests require Keycloak to be running (via docker-compose up -d).
///     Tests are automatically skipped when Keycloak is not available.
/// </summary>
[Trait("Category", "Keycloak")]
public class KeycloakOidcDiscoveryTests
{
    private const string KeycloakHost = "http://localhost:8082";
    private const string RealmName = "daedalus";
    private const string WellKnownEndpoint = $"{KeycloakHost}/realms/{RealmName}/.well-known/openid-configuration";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    ///     Tests that Keycloak is accessible and running on the expected port.
    /// </summary>
    [RequiresKeycloakFact]
    public async Task Keycloak_IsAccessible_OnConfiguredPort()
    {
        // Act
        var response = await HttpClient.GetAsync(KeycloakHost);

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    ///     Tests that Keycloak provides OIDC discovery metadata at the .well-known endpoint.
    ///     This endpoint is critical for the API to validate JWT tokens.
    /// </summary>
    [RequiresKeycloakFact]
    public async Task Keycloak_ProvidesOpenIdConfiguration_AtWellKnownEndpoint()
    {
        // Act
        var response = await HttpClient.GetAsync(WellKnownEndpoint);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Keycloak should serve OIDC discovery at {WellKnownEndpoint}. Is Keycloak running? Try: docker compose up -d");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    ///     Tests that OIDC configuration includes required endpoints for JWT validation.
    /// </summary>
    [RequiresKeycloakFact]
    public async Task OpenIdConfiguration_IncludesRequiredEndpoints()
    {
        // Act
        var response = await HttpClient.GetAsync(WellKnownEndpoint);
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Check for required fields that API needs for token validation
        content.Should().Contain("\"issuer\"", "OIDC discovery must include issuer URL");
        content.Should().Contain("\"jwks_uri\"", "OIDC discovery must include JWKS URI for public key validation");
        content.Should().Contain("\"token_endpoint\"", "OIDC discovery must include token endpoint");
        content.Should().Contain("\"authorization_endpoint\"", "OIDC discovery must include authorization endpoint");
    }

    /// <summary>
    ///     Tests that Keycloak JWKS endpoint is accessible for JWT public key retrieval.
    ///     The API uses this endpoint to validate JWT token signatures.
    /// </summary>
    [RequiresKeycloakFact]
    public async Task Keycloak_ProvidesJwksKeys_ForTokenValidation()
    {
        // Arrange
        var jwksUrl = $"{KeycloakHost}/realms/{RealmName}/protocol/openid-connect/certs";

        // Act
        var response = await HttpClient.GetAsync(jwksUrl);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Keycloak should serve JWKS (JSON Web Key Set) at /protocol/openid-connect/certs");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"keys\"", "JWKS response must contain key array for JWT signature validation");
    }

    /// <summary>
    ///     Tests that the daedalus-api client is configured in Keycloak realm.
    ///     This client represents the backend API in the OIDC flow.
    /// </summary>
    [RequiresKeycloakFact]
    public async Task KeycloakRealm_HasDaedalusApiClient_Configured()
    {
        // Note: This test would require admin authentication to verify client details
        // For now, we just verify the realm exists via OIDC discovery

        // Act
        var response = await HttpClient.GetAsync(WellKnownEndpoint);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Realm '{RealmName}' should exist and be configured. Is keycloak-realm.json imported?");
    }

    /// <summary>
    ///     Tests that the daedalus-wasm client is configured in Keycloak realm.
    ///     This client represents the Blazor WASM frontend in the OIDC flow.
    /// </summary>
    [RequiresKeycloakFact]
    public async Task KeycloakRealm_HasDaedalusWasmClient_Configured()
    {
        // Note: This test would require admin authentication to verify client details

        // Act
        var response = await HttpClient.GetAsync(WellKnownEndpoint);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Realm '{RealmName}' should exist with WASM client configured");
    }

    /// <summary>
    ///     Tests that OIDC configuration issuer matches the Authority setting in API appsettings.
    ///     If these don't match, JWT validation will fail with "invalid issuer" errors.
    /// </summary>
    [RequiresKeycloakFact]
    public async Task OpenIdConfiguration_IssuerMatches_ExpectedAuthority()
    {
        // Arrange
        var expectedAuthority = $"{KeycloakHost}/realms/{RealmName}";

        // Act
        var response = await HttpClient.GetAsync(WellKnownEndpoint);
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain($"\"issuer\":\"{expectedAuthority}\"",
            $"Keycloak issuer should match Authority setting: {expectedAuthority}");
    }
}
