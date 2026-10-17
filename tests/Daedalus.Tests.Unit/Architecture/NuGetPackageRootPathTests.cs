using System.Text.RegularExpressions;

namespace Daedalus.Tests.Unit.Architecture;

/// <summary>
///     Guards MSBuild paths built on <c>$(NuGetPackageRoot)</c>. Locally the property ends with a slash, but inside
///     the run sandbox Thalos sets <c>NUGET_PACKAGES=/work/nuget</c>, so NuGet writes it without one and a bare
///     <c>$(NuGetPackageRoot)zeroalloc.testhelpers/...</c> expands to <c>/work/nugetzeroalloc.testhelpers/...</c>,
///     matching nothing.
/// </summary>
public sealed partial class NuGetPackageRootPathTests
{
    private const int MinimumTestProjects = 7;

    private static readonly string[] ScannedRoots = ["src", "tests"];

    /// <summary><c>$(NuGetPackageRoot)</c> immediately followed by a letter or a slash.</summary>
    [GeneratedRegex(@"\$\(NuGetPackageRoot\)[A-Za-z/\\]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BareConcatenation();

    /// <summary>
    ///     Every project, props and targets file under <c>src/</c> and <c>tests/</c> that appends a path segment to
    ///     <c>$(NuGetPackageRoot)</c> wraps it in <c>EnsureTrailingSlash</c>. Red: change one test project's
    ///     <c>Compile Remove</c> back to the bare <c>$(NuGetPackageRoot)zeroalloc.testhelpers/...</c> form; the
    ///     failure names that file and line. Red: change MinimumTestProjects to 8; the coverage assertion fails.
    /// </summary>
    [Fact]
    public void Every_path_built_on_NuGetPackageRoot_ensures_its_trailing_slash()
    {
        var root = FindRepositoryRoot();
        var files = ScannedRoots
            .SelectMany(top => Directory.EnumerateFiles(Path.Combine(root, top), "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".csproj", StringComparison.Ordinal)
                         || f.EndsWith(".props", StringComparison.Ordinal)
                         || f.EndsWith(".targets", StringComparison.Ordinal))
                        && !Path.GetRelativePath(root, f)
                            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                            .Any(segment => segment is "bin" or "obj"))
            .ToList();

        files.Count(f => f.EndsWith(".csproj", StringComparison.Ordinal)
                         && Path.GetRelativePath(root, f).StartsWith("tests", StringComparison.Ordinal))
            .Should().BeGreaterThanOrEqualTo(MinimumTestProjects, "the scan must cover every test project");

        var offenders = files
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (File: f, Line: i + 1, Text: line))
                .Where(x => BareConcatenation().IsMatch(x.Text)))
            .Select(x => $"{Path.GetRelativePath(root, x.File)}:{x.Line}")
            .ToList();

        string.Join(", ", offenders).Should().BeEmpty(
            "a bare $(NuGetPackageRoot) lacks its trailing slash in the sandbox; use "
            + "$([MSBuild]::EnsureTrailingSlash('$(NuGetPackageRoot)'))");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Daedalus.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not find Daedalus.sln walking up from {AppContext.BaseDirectory}.");
    }
}
