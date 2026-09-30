using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Thalos.Mcp;
using Thalos.Tools;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     The <c>roslyn</c> MCP server's whole tool surface, held against the shipped configuration of both hosts that start
///     it. <c>RoslynCodeLens.Mcp.tools.json</c> is a snapshot of the pinned server's own <c>tools/list</c>, taken from the
///     real server; every tool in it must either be named exactly, as a read tool, in an agent's envelope, or be bound in
///     <c>Thalos:ToolPolicies</c>. <c>DefaultToolAuthorizer</c> allows any tool no binding matches, so a tool that is
///     neither is reachable, unaudited, by any agent whose envelope admits it. That is how <c>load_solution</c> and
///     <c>change_signature</c> sat behind the implementer's <c>roslyn__*</c> before this test.
/// </summary>
/// <remarks>
///     To move the pin: bump the version in every <c>.mcp.json</c>, retake the snapshot from the new server's
///     <c>tools/list</c>, then name each new tool in an envelope or bind it. The pin check below fails until the snapshot
///     matches, so a version bump cannot skip the review of what it adds.
/// </remarks>
public sealed class RoslynCodeLensToolBoundaryTests
{
    private const string Source = "roslyn";

    private const string Architect = "Daedalus Architect";

    public static TheoryData<string, string> Hosts => new()
    {
        { "Daedalus.Api.appsettings.json", "Daedalus.Api.mcp.json" },
        { "Daedalus.Cli.appsettings.json", "Daedalus.Cli.mcp.json" },
    };

    private sealed record Snapshot(string Server, string Version, IReadOnlyList<string> Tools);

    private static Snapshot LoadSnapshot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "RoslynCodeLens.Mcp.tools.json");
        var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), JsonSerializerOptions.Web)!;
        snapshot.Tools.Should().NotBeEmpty("an empty snapshot would pass every check below");
        return snapshot;
    }

    private static IConfiguration Load(string fileName) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(fileName, optional: false)
            .Build();

    private static List<string> Bindings(IConfiguration configuration) =>
        [.. configuration.GetSection("Thalos:ToolPolicies").GetChildren().Select(c => c["Pattern"]!)];

    private static List<(string Name, List<string> Tools)> Envelopes(IConfiguration configuration) =>
        [
            .. configuration.GetSection("Thalos:Agents").GetChildren().Select(a => (
                a["Name"]!,
                a.GetSection("Tools").GetChildren().Select(t => t.Value!).ToList())),
        ];

    private static bool IsGlob(string pattern) => pattern.AsSpan().IndexOfAny('*', '?') >= 0;

    [Theory]
    [MemberData(nameof(Hosts))]
    public void The_roslyn_server_is_pinned_to_the_snapshot_version(string appSettings, string mcpConfig)
    {
        _ = appSettings;
        var snapshot = LoadSnapshot();
        var roslyn = McpConfigFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, mcpConfig)))[Source];
        var pinned = $"{snapshot.Server}@{snapshot.Version}";

        // Red: drop "@2.18.1" from the args; dnx then runs whatever version NuGet resolves, whose tools nobody reviewed.
        roslyn.Args.Should().NotBeNull();
        roslyn.Args![0].Should().Be(pinned, $"{mcpConfig} must start the server the snapshot was taken from");
        if (roslyn.RunScoped is { } runScoped)
        {
            // Red: pin the host args only; a run's own server then floats.
            runScoped.Args.Should().NotBeNull();
            runScoped.Args![0].Should().Be(pinned, $"{mcpConfig}'s run-scoped server must be the same pinned version");
        }
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Every_tool_of_the_pinned_server_is_enumerated_as_a_read_tool_or_bound_to_a_policy(string appSettings, string mcpConfig)
    {
        _ = mcpConfig;
        var configuration = Load(appSettings);
        var bindings = Bindings(configuration);
        var enumerated = Envelopes(configuration)
            .SelectMany(e => e.Tools)
            .Where(t => !IsGlob(t))
            .ToHashSet(StringComparer.Ordinal);

        // Red: remove the roslyn__load_solution binding; load_solution is named in no envelope, so it is then unguarded.
        var unguarded = LoadSnapshot().Tools
            .Select(tool => $"{Source}__{tool}")
            .Where(tool => !enumerated.Contains(tool) && !bindings.Exists(pattern => Glob.IsMatch(pattern, tool)))
            .ToList();
        unguarded.Should().BeEmpty(
            $"{appSettings} must name each of these in an agent's envelope as a read tool, or bind it in Thalos:ToolPolicies");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void No_envelope_but_the_architects_admits_roslyn_tools_by_wildcard(string appSettings, string mcpConfig)
    {
        _ = mcpConfig;
        var tools = LoadSnapshot().Tools.Select(tool => $"{Source}__{tool}").ToList();

        foreach (var (name, envelope) in Envelopes(Load(appSettings)).Where(e => !string.Equals(e.Name, Architect, StringComparison.Ordinal)))
        {
            // Red: put "roslyn__*" back on the implementer or the scout. The Architect keeps it: it answers only an
            // interactive caller, and every tool it could reach that writes or administers the server is bound.
            envelope.Where(t => IsGlob(t) && tools.Exists(tool => Glob.IsMatch(t, tool))).Should().BeEmpty(
                $"{name} in {appSettings} must enumerate its roslyn tools by name, so a new server version adds none to it");
            // Red: misspell one enumerated name, or name a tool the pinned version no longer ships.
            envelope.Where(t => t.StartsWith($"{Source}__", StringComparison.Ordinal) && !IsGlob(t))
                .Should().BeSubsetOf(tools, $"{name} in {appSettings} must name only tools the pinned server has");
        }
    }
}
