using System.Text;

namespace Daedalus.Tests.Playwright.Browser;

/// <summary>
///     Base class for browser-based E2E tests providing Playwright page interaction,
///     HTTP client helpers, and Testcontainers support.
/// </summary>
public abstract class BrowserTestBase : PageTest
{
    /// <summary>
    ///     Whether the browser test environment setup completed successfully.
    /// </summary>
    protected bool SetUpCompleted { get; private set; }

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

    /// <summary>
    ///     Guards all tests in the fixture. A server that tried to start and failed is a <see cref="Assert.Fail(string)"/> —
    ///     the exception already propagated out of the set-up fixture, and this makes the verdict unmistakable if a runner
    ///     ever executes the tests anyway. Only a genuinely absent environment (no Docker daemon, no configured server) is
    ///     Inconclusive, because "skipped" is invisible in a green build.
    /// </summary>
    [OneTimeSetUp]
    public void CheckBrowserTestPrerequisites()
    {
        if (E2EServerFixture.StartupFailure is { } failure)
        {
            Assert.Fail(failure);
        }

        if (!E2EServerFixture.IsBrowserServerReady)
        {
            Assert.Inconclusive(
                E2EServerFixture.BrowserSkipReason
                ?? "Browser E2E tests require the full E2E environment. Set E2E_BASE_URL to a running frontend.");
        }
    }

    [SetUp]
    public virtual async Task SetUpAsync()
    {
        SetUpCompleted = false;

        // Verify Playwright has initialized the Page and Context (from PageTest base class)
        if (Page == null || Context == null)
        {
            Assert.Inconclusive(
                "Playwright browser is not initialized. Ensure browsers are installed: npx playwright install --with-deps");
            return;
        }

        // Use the server URL from the global fixture
        if (E2EServerFixture.ServerUrl == null)
        {
            Assert.Inconclusive("E2E server URL is not configured.");
            return;
        }

        BaseUrl = E2EServerFixture.ServerUrl;

        // Set the connection string from the fixture
        if (E2EServerFixture.IsServerReady)
        {
            DbConnectionString = E2EServerFixture.ConnectionString;
        }

        // Wait for server to be ready before running tests
        await WaitForServerReadyAsync(15, 1000).ConfigureAwait(false);

        // Initialize PostgreSQL container if needed (for integration tests)
        await InitializeDatabaseAsync().ConfigureAwait(false);

        await Context
            .SetExtraHTTPHeadersAsync(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Accept-Language"] = "en-US" })
            .ConfigureAwait(false);

        // Enable tracing for debugging
        await Context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true
        }).ConfigureAwait(false);

        SetUpCompleted = true;
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
        // Only stop tracing if setup completed (Context and tracing were initialized)
        if (SetUpCompleted)
        {
            try
            {
                var tracingDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "traces");
                Directory.CreateDirectory(tracingDir);

                await Context.Tracing.StopAsync(new TracingStopOptions
                {
                    Path = Path.Combine(tracingDir,
                        $"{TestContext.CurrentContext.Test.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.zip")
                }).ConfigureAwait(false);
            }
            catch
            {
                // Ignore tracing errors
            }
        }

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
    ///     Writes a full-page screenshot to <c>{WorkDirectory}/regression-screenshots/</c> by default so normal test runs
    ///     do not dirty the working tree. When <c>DAEDALUS_REGRESSION_SCREENSHOTS=1</c> is set it writes into
    ///     <c>docs/regression-screenshots/</c> at the repository root (found by walking up to <c>Daedalus.sln</c>) so a
    ///     regression report can link it. <paramref name="relativePath"/> may contain sub-directories (<c>2026-08-16/home.png</c>).
    /// </summary>
    protected async Task SaveRegressionScreenshotAsync(string relativePath)
    {
        var screenshotsDir = ResolveRegressionScreenshotsDirectory();
        if (screenshotsDir is null)
        {
            await TestContext.Progress.WriteLineAsync("Daedalus.sln not found above the test directory; regression screenshot skipped.").ConfigureAwait(false);
            return;
        }

        var path = Path.Combine(screenshotsDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await Page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true }).ConfigureAwait(false);
    }

    private static string? ResolveRegressionScreenshotsDirectory()
    {
        var publishToDocs = string.Equals(
            Environment.GetEnvironmentVariable("DAEDALUS_REGRESSION_SCREENSHOTS"), "1", StringComparison.Ordinal);
        if (!publishToDocs)
        {
            return Path.Combine(TestContext.CurrentContext.WorkDirectory, "regression-screenshots");
        }

        var dir = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Daedalus.sln")))
        {
            dir = dir.Parent;
        }

        return dir is null ? null : Path.Combine(dir.FullName, "docs", "regression-screenshots");
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

    /// <summary>
    ///     Navigates to a path relative to the base URL.
    /// </summary>
    protected async Task NavigateToAsync(string path)
    {
        var url = new Uri(BaseUrl, path);
        await Page.GotoAsync(url.ToString()).ConfigureAwait(false);
    }

    /// <summary>
    ///     Waits for an element to be visible.
    /// </summary>
    protected async Task<ILocator> WaitForElementAsync(string selector, int timeoutMs = 5000)
    {
        var locator = Page.Locator(selector);
        await locator.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeoutMs
        }).ConfigureAwait(false);
        return locator;
    }

    /// <summary>
    ///     Waits for an element to be hidden.
    /// </summary>
    protected async Task WaitForElementHiddenAsync(string selector, int timeoutMs = 5000)
    {
        var locator = Page.Locator(selector);
        await locator.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = timeoutMs
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     Fills an input field with text.
    /// </summary>
    protected async Task FillInputAsync(string selector, string text) =>
        await Page.Locator(selector).FillAsync(text).ConfigureAwait(false);

    /// <summary>
    ///     Clicks an element.
    /// </summary>
    protected async Task ClickElementAsync(string selector) =>
        await Page.Locator(selector).ClickAsync().ConfigureAwait(false);

    /// <summary>
    ///     Gets the text content of an element.
    /// </summary>
    protected async Task<string> GetElementTextAsync(string selector) =>
        await Page.Locator(selector).TextContentAsync().ConfigureAwait(false) ?? string.Empty;

    /// <summary>
    ///     Checks if an element is visible.
    /// </summary>
    protected async Task<bool> IsElementVisibleAsync(string selector) =>
        await Page.Locator(selector).IsVisibleAsync().ConfigureAwait(false);

    /// <summary>
    ///     Takes a screenshot for debugging.
    /// </summary>
    protected async Task TakeScreenshotAsync(string name)
    {
        var screenshotsDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "screenshots");
        Directory.CreateDirectory(screenshotsDir);

        await Page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(screenshotsDir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.png"),
            FullPage = true
        }).ConfigureAwait(false);
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
