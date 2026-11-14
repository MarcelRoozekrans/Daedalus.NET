using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Playwright.Api;

/// <summary>
///     NUnit SetUpFixture that manages the lifecycle of the test server and database for the whole suite.
///     This runs once before all tests and once after all tests in the namespace.
///     By default it hosts the Api in memory over a Postgres container; with <c>E2E_BASE_URL</c> and
///     <c>E2E_CONNECTION_STRING</c> set it runs the suite against that already running host instead
///     (see <see cref="E2EServer"/>). Tests access either through <see cref="CreateClient"/>.
/// </summary>
[SetUpFixture]
public class E2EServerFixture
{
    private static E2EServer? _server;

    /// <summary>
    ///     The base URL of the server under test: the external host's, or a placeholder for the in-memory TestServer.
    /// </summary>
    public static Uri? ServerUrl => _server?.ServerUrl;

    /// <summary>
    ///     The connection string for the test database.
    /// </summary>
    public static string ConnectionString => _server?.ConnectionString ?? string.Empty;

    /// <summary>
    ///     Indicates whether the server is ready for tests.
    /// </summary>
    public static bool IsServerReady => _server?.IsReady ?? false;

    /// <summary>
    ///     The port of <see cref="ServerUrl"/> (not used by TestServer).
    /// </summary>
    public static int Port => _server?.Port ?? 0;

    /// <summary>
    ///     Creates an HttpClient configured to communicate with the server under test.
    /// </summary>
    public static HttpClient CreateClient()
    {
        if (_server == null)
        {
            throw new InvalidOperationException(
                "Test server is not initialized. Ensure E2EServerFixture has completed setup.");
        }

        return _server.CreateClient();
    }

    [OneTimeSetUp]
    public async Task GlobalSetupAsync()
    {
        _server = new E2EServer(name => Environment.GetEnvironmentVariable(name));
        try
        {
            await _server.StartAsync().ConfigureAwait(false);
            await TestContext.Progress.WriteLineAsync(_server.HostsInProcess
                    ? $"E2E Test Server started at {ServerUrl}"
                    : $"E2E tests run against the external host at {ServerUrl}")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TestContext.Progress.WriteLineAsync($"Failed to start E2E Test Server: {ex.Message}")
                .ConfigureAwait(false);
            throw;
        }
    }

    [OneTimeTearDown]
    public async Task GlobalTeardownAsync()
    {
        if (_server != null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        await TestContext.Progress.WriteLineAsync("E2E Test Server stopped").ConfigureAwait(false);
    }
}
