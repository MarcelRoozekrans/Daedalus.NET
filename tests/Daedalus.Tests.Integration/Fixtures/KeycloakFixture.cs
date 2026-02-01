using System.Text;
using System.Text.Json;
using Testcontainers.Keycloak;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Keycloak test fixture using Testcontainers.
///     Automatically starts a Keycloak container with realm import for integration tests.
///     Provides helper methods for obtaining access tokens.
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    private const string RealmName = "daedalus";
    private const string DefaultClientId = "daedalus-api";
    private const string DefaultClientSecret = "daedalus-api-secret-change-in-production";
    private const string DefaultTestUsername = "dev";
    private const string DefaultTestPassword = "dev123";

    private readonly KeycloakContainer _container;

    public KeycloakFixture()
    {
        // Resolve keycloak-realm.json from the repository root
        var realmFilePath = FindRealmConfigPath();

        _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.1")
            .WithRealm(realmFilePath)
            .WithCleanUp(true)
            .Build();
    }

    /// <summary>
    ///     Gets the base URL of the Keycloak instance (e.g., http://localhost:32768/).
    /// </summary>
    public string BaseUrl => _container.GetBaseAddress().TrimEnd('/');

    /// <summary>
    ///     Gets the realm name configured in the fixture.
    /// </summary>
    public static string Realm => RealmName;

    /// <summary>
    ///     Gets the OIDC well-known endpoint for the test realm.
    /// </summary>
    public string WellKnownEndpoint => $"{BaseUrl}/realms/{RealmName}/.well-known/openid-configuration";

    /// <summary>
    ///     Gets the token endpoint for the test realm.
    /// </summary>
    public string TokenEndpoint => $"{BaseUrl}/realms/{RealmName}/protocol/openid-connect/token";

    /// <summary>
    ///     Gets the JWKS endpoint for the test realm.
    /// </summary>
    public string JwksEndpoint => $"{BaseUrl}/realms/{RealmName}/protocol/openid-connect/certs";

    /// <summary>
    ///     Gets the expected authority URL for JWT validation.
    /// </summary>
    public string Authority => $"{BaseUrl}/realms/{RealmName}";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        System.Console.WriteLine($"✓ Keycloak container started at {BaseUrl} (realm: {RealmName})");

        // Enable service accounts (client_credentials flow) for daedalus-api via Admin REST API.
        // Cannot set serviceAccountsEnabled=true in realm JSON due to Keycloak 26.x import bug
        // ("Session not bound to a realm"), so we enable it post-startup.
        await EnableServiceAccountsAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    ///     Obtains an access token using OAuth 2.0 Client Credentials Flow.
    /// </summary>
    public async Task<string> ObtainAccessTokenAsync(
        string? clientId = null,
        string? clientSecret = null)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "grant_type", "client_credentials" },
                { "client_id", clientId ?? DefaultClientId },
                { "client_secret", clientSecret ?? DefaultClientSecret }
            })
        };

        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        return ExtractTokenFromResponse(content);
    }

    /// <summary>
    ///     Obtains an access token using OAuth 2.0 Resource Owner Password Flow.
    /// </summary>
    public async Task<string> ObtainUserAccessTokenAsync(
        string? username = null,
        string? password = null,
        string? clientId = null,
        string? clientSecret = null)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "grant_type", "password" },
                { "client_id", clientId ?? DefaultClientId },
                { "client_secret", clientSecret ?? DefaultClientSecret },
                { "username", username ?? DefaultTestUsername },
                { "password", password ?? DefaultTestPassword },
                { "scope", "openid profile email" }
            })
        };

        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        return ExtractTokenFromResponse(content);
    }

    /// <summary>
    ///     Decodes a Base64Url-encoded JWT payload segment.
    /// </summary>
    public static string DecodeBase64Url(string base64UrlString)
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

    /// <summary>
    ///     Enables service accounts (client_credentials flow) for the daedalus-api client
    ///     via the Keycloak Admin REST API. This is required because Keycloak 26.x has a
    ///     realm import bug when serviceAccountsEnabled=true in the JSON.
    /// </summary>
    private async Task EnableServiceAccountsAsync()
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // 1. Get admin token from the master realm
        var adminTokenRequest = new HttpRequestMessage(HttpMethod.Post,
            $"{BaseUrl}/realms/master/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "grant_type", "password" },
                { "client_id", "admin-cli" },
                { "username", "admin" },
                { "password", "admin" }
            })
        };

        var adminTokenResponse = await httpClient.SendAsync(adminTokenRequest);
        adminTokenResponse.EnsureSuccessStatusCode();
        var adminTokenJson = await adminTokenResponse.Content.ReadAsStringAsync();
        var adminToken = ExtractTokenFromResponse(adminTokenJson);

        // 2. Find the daedalus-api client's internal ID
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var clientsResponse = await httpClient.GetAsync(
            $"{BaseUrl}/admin/realms/{RealmName}/clients?clientId={DefaultClientId}");
        clientsResponse.EnsureSuccessStatusCode();

        var clientsJson = await clientsResponse.Content.ReadAsStringAsync();
        using var clientsDoc = JsonDocument.Parse(clientsJson);
        var clientInternalId = clientsDoc.RootElement[0].GetProperty("id").GetString()!;

        // 3. Enable service accounts on the client
        var updatePayload = new StringContent(
            JsonSerializer.Serialize(new { serviceAccountsEnabled = true }),
            Encoding.UTF8,
            "application/json");

        var updateResponse = await httpClient.PutAsync(
            $"{BaseUrl}/admin/realms/{RealmName}/clients/{clientInternalId}",
            updatePayload);
        updateResponse.EnsureSuccessStatusCode();

        System.Console.WriteLine($"✓ Service accounts enabled for client '{DefaultClientId}'");
    }

    private static string ExtractTokenFromResponse(string jsonResponse)
    {
        const string tokenPrefix = "\"access_token\":\"";
        var tokenStart = jsonResponse.IndexOf(tokenPrefix, StringComparison.Ordinal) + tokenPrefix.Length;
        var remainingJson = jsonResponse.AsSpan(tokenStart);
        var tokenEnd = remainingJson.IndexOf('"');
        return jsonResponse[tokenStart..(tokenStart + tokenEnd)];
    }

    /// <summary>
    ///     Locates keycloak-realm.json by walking up from the test project directory.
    /// </summary>
    private static string FindRealmConfigPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        // Walk up from bin/Debug/net10.0 to find the repo root
        while (directory != null)
        {
            var realmFile = Path.Combine(directory.FullName, "keycloak-realm.json");
            if (File.Exists(realmFile))
            {
                return realmFile;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not find keycloak-realm.json in any parent directory. " +
            "Ensure the file exists at the repository root.");
    }
}
