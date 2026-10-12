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
///     as a post-configuration, after every configuration source is bound, and gives every rule the factory could pick
///     for the category a filter that refuses it, so whichever rule it picks refuses the line too. Only those rules are
///     wrapped, so no other category pays for a filter call. The added category rule, wrapped the same way, covers a
///     provider no rule names.
/// </remarks>
internal static class HostingDiagnosticsLogFilter
{
    /// <summary>The category of ASP.NET Core's per-request hosting log lines.</summary>
    internal const string Category = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>Wraps every rule of <paramref name="options"/> that could apply to the category, and adds the category rule.</summary>
    internal static void Apply(LoggerFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var rules = options.Rules.Select(rule => CouldApply(rule) ? Wrap(rule) : rule).ToList();
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

    /// <summary>
    ///     Whether the logger factory could pick <paramref name="rule"/> for the category: a rule with no category, a
    ///     wildcard category, or a category the category starts with, compared as the factory compares them, ignoring case.
    /// </summary>
    private static bool CouldApply(LoggerFilterRule rule) =>
        rule.CategoryName is not { } category
        || category.Contains('*', StringComparison.Ordinal)
        || Category.StartsWith(category, StringComparison.OrdinalIgnoreCase);

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
