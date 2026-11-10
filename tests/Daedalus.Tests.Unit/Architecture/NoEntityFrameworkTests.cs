using System.Text.Json;

namespace Daedalus.Tests.Unit.Architecture;

/// <summary>
///     Phase 3.5 moved every store to ZeroAlloc.ORM and removed EF Core. No project under <c>src/</c> or <c>tests/</c>
///     may resolve an EF Core package again, directly or through another package, so the check reads what NuGet
///     restored, each project's <c>obj/project.assets.json</c>, not the project files.
/// </summary>
public sealed class NoEntityFrameworkTests
{
    /// <summary>The 11 projects under <c>src/</c> and the 7 under <c>tests/</c>, every project in <c>Daedalus.sln</c>.</summary>
    private const int MinimumProjects = 18;

    private static readonly string[] ScannedRoots = ["src", "tests"];

    private static readonly string[] ForbiddenExactIds =
    [
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "ZeroAlloc.Outbox.EfCore",
        "ZeroAlloc.ValueObjects.EfCore",
    ];

    private const string ForbiddenPrefix = "Microsoft.EntityFrameworkCore";

    /// <summary>
    ///     Every project's restore graph is free of EF Core. The assets file lists each library a project resolves,
    ///     transitive ones included, as <c>Id/Version</c>.
    ///     Red: add <c>&lt;PackageReference Include="Microsoft.EntityFrameworkCore" /&gt;</c> back to
    ///     <c>Daedalus.Infrastructure.csproj</c> and restore; every project that references Infrastructure names it.
    ///     Red: delete one project's <c>obj/project.assets.json</c>; the test fails naming the missing file instead of
    ///     passing on fewer projects. Red: raise MinimumProjects to 19; the coverage assertion fails.
    /// </summary>
    [Fact]
    public void No_project_resolves_entity_framework()
    {
        var root = FindRepositoryRoot();
        var projects = ScannedRoots
            .SelectMany(top => Directory.EnumerateFiles(Path.Combine(root, top), "*.csproj", SearchOption.AllDirectories))
            .Where(f => !Path.GetRelativePath(root, f)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                .Any(segment => segment is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToList();

        projects.Count.Should().BeGreaterThanOrEqualTo(MinimumProjects, "the scan must cover every project in the solution");

        var missing = new List<string>();
        var offenders = new List<string>();
        foreach (var project in projects)
        {
            var assets = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
            if (!File.Exists(assets))
            {
                missing.Add(Path.GetRelativePath(root, assets));
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(assets));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries)
                || libraries.ValueKind != JsonValueKind.Object)
            {
                missing.Add($"{Path.GetRelativePath(root, assets)} (no libraries section)");
                continue;
            }

            offenders.AddRange(libraries.EnumerateObject()
                .Select(library => library.Name)
                .Where(IsEntityFramework)
                .Select(library => $"{Path.GetFileNameWithoutExtension(project)}: {library}"));
        }

        string.Join(", ", missing).Should().BeEmpty(
            "every project must be restored, or the scan would pass on the projects it could not read; "
            + "run dotnet restore Daedalus.sln first");
        string.Join(Environment.NewLine, offenders).Should().BeEmpty(
            "phase 3.5 removed EF Core; no project may resolve it again, directly or transitively");
    }

    private static bool IsEntityFramework(string library)
    {
        var slash = library.IndexOf('/', StringComparison.Ordinal);
        var id = slash < 0 ? library : library[..slash];
        return id.StartsWith(ForbiddenPrefix, StringComparison.OrdinalIgnoreCase)
            || ForbiddenExactIds.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Walks up from <see cref="AppContext.BaseDirectory"/> to the folder holding <c>Daedalus.sln</c>.</summary>
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
