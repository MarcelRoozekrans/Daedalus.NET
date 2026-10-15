using System.Reflection;

namespace Daedalus.Web.Services;

/// <summary>
///     The version the footer shows: the console assembly's informational version, which the build derives from git
///     (GitVersion in CI, <c>VersionPrefix</c> locally), without the <c>+commit</c> suffix the SDK appends.
/// </summary>
public static class AppVersion
{
    /// <summary>This console's version, or null when the assembly carries none.</summary>
    public static string? Shown { get; } =
        Display(typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    ///     <paramref name="informational"/> up to any <c>+</c> build metadata, such as the source revision; null for a
    ///     missing or blank one.
    /// </summary>
    public static string? Display(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return null;
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        var version = (plus < 0 ? informational : informational[..plus]).Trim();
        return version.Length == 0 ? null : version;
    }
}
