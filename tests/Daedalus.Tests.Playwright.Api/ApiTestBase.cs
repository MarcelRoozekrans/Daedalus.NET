using System.Text;

namespace Daedalus.Tests.Playwright.Api;

/// <summary>
///     Base class for API-only E2E tests providing HTTP client helpers
///     and optional Testcontainers support, without any Playwright browser dependency.
/// </summary>
public abstract class ApiTestBase
{
    /// <summary>
    ///     Base URL for the application under test.
    /// </summary>
    protected Uri BaseUrl { get; private set; } = null!;

    /// <summary>
    ///     PostgreSQL container for test database.
    /// </summary>
    protected PostgreSqlContainer? PostgresContainer { get; private set; }

    /// <summary>
    ///     Connection string for the test database.
    /// </summary>
    protected string? DbConnectionString { get; private set; }

    [SetUp]
    public virtual async Task SetUpAsync()
    {
        // Use the server URL from the global fixture, or fall back to environment variable
        Uri? serverUrl = null;

        if (E2EServerFixture.IsServerReady && E2EServerFixture.ServerUrl != null)
        {
            serverUrl = E2EServerFixture.ServerUrl;
        }
        else
        {
            var envUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL", EnvironmentVariableTarget.Process);
            if (!string.IsNullOrEmpty(envUrl))
            {
                serverUrl = new Uri(envUrl);
            }
        }

        if (serverUrl == null)
        {
            Assert.Inconclusive(
                "E2E test server is not available. Start the server or run tests with the E2E_BASE_URL environment variable set.");
            return;
        }

        BaseUrl = serverUrl;

        // Set the connection string from the fixture
        if (E2EServerFixture.IsServerReady)
        {
            DbConnectionString = E2EServerFixture.ConnectionString;
        }

        // Wait for server to be ready before running tests (quick check)
        await WaitForServerReadyAsync(5, 500).ConfigureAwait(false);

        // Initialize PostgreSQL container if needed (for integration tests)
        await InitializeDatabaseAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Waits for the server to be ready and responding to requests.
    /// </summary>
    private async Task WaitForServerReadyAsync(int maxRetries = 30, int delayMs = 1000)
    {
#pragma warning disable MA0039 // Do not write your own certificate validation method (needed for local HTTPS testing)
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
        {
            // Accept all certificates for testing
            return true;
        };
#pragma warning restore MA0039

        using var client = new HttpClient(handler, true);
        client.Timeout = TimeSpan.FromSeconds(5);

        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                var healthUrl = new Uri(BaseUrl, "/health");
                var response = await client.GetAsync(healthUrl, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return; // Server is ready
                }
            }
            catch
            {
                // Server not ready yet, will retry
            }

            await Task.Delay(delayMs).ConfigureAwait(false);
        }

        Assert.Inconclusive(
            $"Server at {BaseUrl} did not become ready within {maxRetries * delayMs}ms. Ensure the E2E test server is running or use the E2E_BASE_URL environment variable.");
    }

    [TearDown]
    public virtual async Task TearDownAsync()
    {
        // Dispose PostgreSQL container and reset the field
        if (PostgresContainer != null)
        {
            await PostgresContainer.DisposeAsync().ConfigureAwait(false);
            PostgresContainer = null;
            DbConnectionString = null;
        }

        // Dispose HttpClient for API requests
        _apiClient?.Dispose();
        _apiClient = null;
    }

    /// <summary>
    ///     Initializes the database container for integration tests.
    ///     Override this method to enable database testing.
    /// </summary>
    protected virtual async Task InitializeDatabaseAsync()
    {
        // Default: no database initialization
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    ///     Creates and starts a PostgreSQL container for the test.
    /// </summary>
    protected async Task<string> StartPostgresContainerAsync(
        string database = "testdb",
        string username = "testuser",
        string password = "testpass",
        string? image = null)
    {
        if (PostgresContainer != null)
        {
            throw new InvalidOperationException("PostgreSQL container is already running for this test.");
        }

        var builder = string.IsNullOrEmpty(image)
#pragma warning disable CS0618 // Using PostgreSqlBuilder with parameter - obsolete warning for parameterless constructor
            ? new PostgreSqlBuilder()
            : new PostgreSqlBuilder().WithImage(image);
#pragma warning restore CS0618

        PostgresContainer = builder
            .WithDatabase(database)
            .WithUsername(username)
            .WithPassword(password)
            .Build();

        await PostgresContainer.StartAsync().ConfigureAwait(false);
        DbConnectionString = PostgresContainer.GetConnectionString();

        return DbConnectionString;
    }

    /// <summary>
    ///     Executes a SQL script against the test database.
    /// </summary>
    protected async Task ExecuteSqlAsync(string sql)
    {
        if (PostgresContainer == null)
        {
            throw new InvalidOperationException("PostgreSQL container is not initialized.");
        }

        await PostgresContainer.ExecScriptAsync(sql).ConfigureAwait(false);
    }

    #region API Testing Helpers

    /// <summary>
    ///     JSON serializer options for API response deserialization.
    /// </summary>
    protected static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    ///     Gets an HttpClient configured to talk to the in-memory TestServer.
    /// </summary>
    private HttpClient ApiClient => _apiClient ??= E2EServerFixture.CreateClient();

    private HttpClient? _apiClient;

    /// <summary>
    ///     Makes a GET request to an API endpoint and deserializes the response.
    /// </summary>
    protected async Task<T?> GetApiAsync<T>(string path)
    {
        var response = await ApiClient.GetAsync(path).ConfigureAwait(false);
        ((int)response.StatusCode).Should()
            .BeInRange(200, 299, $"GET {path} should succeed, got {(int)response.StatusCode}");
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, ApiJsonOptions);
    }

    /// <summary>
    ///     Makes a GET request and returns an ApiResponse wrapper.
    /// </summary>
    protected async Task<ApiResponse> GetApiResponseAsync(string path)
    {
        var response = await ApiClient.GetAsync(path).ConfigureAwait(false);
        return new ApiResponse(response);
    }

    /// <summary>
    ///     Makes a POST request to an API endpoint.
    /// </summary>
    protected async Task<ApiResponse> PostApiAsync(string path, object data)
    {
        var json = JsonSerializer.Serialize(data, ApiJsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await ApiClient.PostAsync(path, content).ConfigureAwait(false);
        return new ApiResponse(response);
    }

    /// <summary>
    ///     Makes a PUT request to an API endpoint.
    /// </summary>
    protected async Task<ApiResponse> PutApiAsync(string path, object data)
    {
        var json = JsonSerializer.Serialize(data, ApiJsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await ApiClient.PutAsync(path, content).ConfigureAwait(false);
        return new ApiResponse(response);
    }

    /// <summary>
    ///     Makes a DELETE request to an API endpoint.
    /// </summary>
    protected async Task<ApiResponse> DeleteApiAsync(string path)
    {
        var response = await ApiClient.DeleteAsync(path).ConfigureAwait(false);
        return new ApiResponse(response);
    }

    #endregion
}
