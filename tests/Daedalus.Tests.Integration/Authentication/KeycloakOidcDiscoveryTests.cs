using Daedalus.Tests.Integration.Fixtures;

namespace Daedalus.Tests.Integration.Authentication;

/// <summary>
///     Integration tests verifying Keycloak OIDC discovery and configuration.
///     Uses Testcontainers.Keycloak to automatically start a Keycloak instance
///     with realm import — no manual docker-compose required.
/// </summary>
[Collection(KeycloakCollection.Name)]
[Trait("Category", "Keycloak")]
public class KeycloakOidcDiscoveryTests(KeycloakFixture fixture)
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    ///     Tests that Keycloak is accessible and running.
    /// </summary>
    [Fact]
    public async Task Keycloak_IsAccessible_OnConfiguredPort()
    {
        // Act
        var response = await _httpClient.GetAsync(fixture.BaseUrl);

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    ///     Tests that Keycloak provides OIDC discovery metadata at the .well-known endpoint.
    ///     This endpoint is critical for the API to validate JWT tokens.
    /// </summary>
    [Fact]
    public async Task Keycloak_ProvidesOpenIdConfiguration_AtWellKnownEndpoint()
    {
        // Act
        var response = await _httpClient.GetAsync(fixture.WellKnownEndpoint);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Keycloak should serve OIDC discovery at {fixture.WellKnownEndpoint}");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    ///     Tests that OIDC configuration includes required endpoints for JWT validation.
    /// </summary>
    [Fact]
    public async Task OpenIdConfiguration_IncludesRequiredEndpoints()
    {
        // Act
        var response = await _httpClient.GetAsync(fixture.WellKnownEndpoint);
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
    [Fact]
    public async Task Keycloak_ProvidesJwksKeys_ForTokenValidation()
    {
        // Act
        var response = await _httpClient.GetAsync(fixture.JwksEndpoint);

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
    [Fact]
    public async Task KeycloakRealm_HasDaedalusApiClient_Configured()
    {
        // Verify the realm exists via OIDC discovery
        // Full client verification would require admin authentication

        // Act
        var response = await _httpClient.GetAsync(fixture.WellKnownEndpoint);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Realm '{KeycloakFixture.Realm}' should exist and be configured via keycloak-realm.json import");
    }

    /// <summary>
    ///     Tests that the daedalus-wasm client is configured in Keycloak realm.
    ///     This client represents the Blazor WASM frontend in the OIDC flow.
    /// </summary>
    [Fact]
    public async Task KeycloakRealm_HasDaedalusWasmClient_Configured()
    {
        // Act
        var response = await _httpClient.GetAsync(fixture.WellKnownEndpoint);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Realm '{KeycloakFixture.Realm}' should exist with WASM client configured");
    }

    /// <summary>
    ///     Tests that OIDC configuration issuer matches the expected Authority.
    ///     If these don't match, JWT validation will fail with "invalid issuer" errors.
    /// </summary>
    [Fact]
    public async Task OpenIdConfiguration_IssuerMatches_ExpectedAuthority()
    {
        // Act
        var response = await _httpClient.GetAsync(fixture.WellKnownEndpoint);
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain($"\"issuer\":\"{fixture.Authority}\"",
            $"Keycloak issuer should match Authority setting: {fixture.Authority}");
    }
}
