using System.Net.Sockets;

namespace Daedalus.Tests.Integration.Attributes;

/// <summary>
///     Checks TCP port availability for conditional test execution.
///     Results are cached per port for the lifetime of the test run.
///     Note: Keycloak availability is no longer checked here — Keycloak tests
///     use Testcontainers.Keycloak via <see cref="Fixtures.KeycloakFixture" />.
/// </summary>
internal static class ServiceAvailabilityChecker
{
    private static readonly Lazy<bool> ApiAvailable = new(static () => IsPortOpen(8080));

    public static bool IsApiRunning => ApiAvailable.Value;

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect("localhost", port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
            if (success)
            {
                client.EndConnect(result);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
///     Skips test when API is not running on localhost:8080.
///     Use for tests that only need the running API.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresApiFactAttribute : FactAttribute
{
    public RequiresApiFactAttribute()
    {
        if (!ServiceAvailabilityChecker.IsApiRunning)
        {
            Skip = "API not available on localhost:8080 (requires dotnet run --project src/Daedalus.Api)";
        }
    }
}

/// <summary>
///     Skips theory when API is not running on localhost:8080.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresApiTheoryAttribute : TheoryAttribute
{
    public RequiresApiTheoryAttribute()
    {
        if (!ServiceAvailabilityChecker.IsApiRunning)
        {
            Skip = "API not available on localhost:8080 (requires dotnet run --project src/Daedalus.Api)";
        }
    }
}
