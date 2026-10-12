using Daedalus.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.ServiceDefaults;

/// <summary>
///     <c>AddServiceDefaults</c> keeps the hosting diagnostics' request lines, which carry the query string and so a hub
///     connection's access token, out of every provider, whatever the logging configuration says.
/// </summary>
public sealed class HostingDiagnosticsLogFilterTests
{
    /// <summary>
    ///     Configuration that turns everything on, for all providers or for one, by default alone or by category too,
    ///     still leaves the category off below Warning, while a sibling category follows the configuration and Warning
    ///     still gets through.
    ///     Red: register a plain AddFilter rule instead of the post-configuration: the Console and OpenTelemetry cases
    ///     log at Information. Red: remove the filter: every case logs at Information.
    ///     Red: refuse every level in the filter: Warning is refused.
    ///     Red: drop the original rules in Apply: the sibling category is off at Trace.
    ///     Red: skip the null-category rule when wrapping: the Console Default-only case logs at Information.
    /// </summary>
    [Theory]
    [InlineData("Logging:LogLevel", false)]
    [InlineData("Logging:Console:LogLevel", false)]
    [InlineData("Logging:OpenTelemetry:LogLevel", false)]
    [InlineData("Logging:Console:LogLevel", true)]
    public void No_configuration_turns_the_request_lines_back_on(string section, bool defaultOnly)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal) { [$"{section}:Default"] = "Trace" };
        if (!defaultOnly)
        {
            settings[$"{section}:Microsoft"] = "Trace";
            settings[$"{section}:{HostingDiagnosticsLogFilter.Category}"] = "Trace";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConfiguration(configuration.GetSection("Logging")));
        services.AddServiceDefaults(configuration);
        using var provider = services.BuildServiceProvider();
        var loggers = provider.GetRequiredService<ILoggerFactory>();

        var hosting = loggers.CreateLogger(HostingDiagnosticsLogFilter.Category);

        loggers.CreateLogger("Microsoft.AspNetCore.Routing").IsEnabled(LogLevel.Trace).Should().BeTrue();
        hosting.IsEnabled(LogLevel.Information).Should().BeFalse();
        hosting.IsEnabled(LogLevel.Warning).Should().BeTrue();
    }
}
