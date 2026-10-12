using Microsoft.Extensions.Logging;

namespace Daedalus.ServiceDefaults;

/// <summary>
///     Keeps ASP.NET Core's hosting diagnostics category below <see cref="LogLevel.Warning"/> out of every logging
///     provider. Its "Request starting" line carries the query string, and the console's hub connection carries its
///     bearer token there, because a browser cannot set headers on a WebSocket.
/// </summary>
/// <remarks>
///     A plain <c>AddFilter</c> rule is not enough: the logger factory prefers any rule that names a provider, so a
///     setting such as <c>Logging:Console:LogLevel:Default</c> would turn the line back on for that provider. This runs
///     as a post-configuration, after every configuration source is bound, and gives every rule a filter that refuses
///     the category, so whichever rule the factory picks refuses it too. The added category rule, wrapped the same way,
///     covers a provider no rule names.
/// </remarks>
internal static class HostingDiagnosticsLogFilter
{
    /// <summary>The category of ASP.NET Core's per-request hosting log lines.</summary>
    internal const string Category = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>Wraps every rule of <paramref name="options"/> and adds the category rule.</summary>
    internal static void Apply(LoggerFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var rules = options.Rules.Select(Wrap).ToList();
        rules.Add(Wrap(new LoggerFilterRule(null, Category, LogLevel.Warning, null)));
        options.Rules.Clear();
        foreach (var rule in rules)
        {
            options.Rules.Add(rule);
        }
    }

    /// <summary>Whether a line of <paramref name="category"/> at <paramref name="level"/> is refused.</summary>
    internal static bool Refuses(string? category, LogLevel level) =>
        level < LogLevel.Warning
        && category is not null
        && (string.Equals(category, Category, StringComparison.Ordinal)
            || category.StartsWith(Category + ".", StringComparison.Ordinal));

    private static LoggerFilterRule Wrap(LoggerFilterRule rule)
    {
        var inner = rule.Filter;
        return new LoggerFilterRule(
            rule.ProviderName,
            rule.CategoryName,
            rule.LogLevel,
            (provider, category, level) => !Refuses(category, level) && (inner?.Invoke(provider, category, level) ?? true));
    }
}
