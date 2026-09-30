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
///     <c>change_signature</c> sat behind the implementer's <c>roslyn__*</c> before this test. The tools that write or
///     administer the server are classified here by name, in <see cref="WriteTools"/> and <see cref="OperatorTools"/>:
///     each must be bound in both hosts whatever the envelopes say, and no restricted envelope may offer one but the
///     implementer's <c>apply_code_action</c>. <c>RunScopedRoslynRoutingTests</c> holds the snapshot against the live server.
/// </summary>
/// <remarks>
///     To move the pin: bump the version in every <c>.mcp.json</c>, retake the snapshot from the new server's
///     <c>tools/list</c>, classify any new tool that writes or administers, then name each new tool in an envelope or bind
///     it. The pin check below fails until the snapshot's version matches, and the live check in
///     <c>RunScopedRoslynRoutingTests</c> fails until its tool list does, so a version bump cannot skip that review.
/// </remarks>
public sealed class RoslynCodeLensToolBoundaryTests
{
    private const string Source = "roslyn";

    private const string Architect = "Daedalus Architect";

    private const string Implementer = "implementer";

    /// <summary>
    ///     The pinned server's tools that write to disk, each behind a <c>preview</c> flag an agent can turn off. Reviewed
    ///     against the snapshot's descriptions; a version bump that adds one must add it here.
    /// </summary>
    private static readonly string[] WriteTools = ["apply_code_action", "change_signature", "rename_symbol"];

    /// <summary>
    ///     The pinned server's administration tools: which solution it serves, whose analyzers it trusts, when it
    ///     re-evaluates MSBuild, and <c>find_breaking_changes</c>, which reads a baseline from any host path.
    /// </summary>
    private static readonly string[] OperatorTools =
    [
        "load_solution", "unload_solution", "set_active_solution", "trust_solution", "revoke_trust", "list_trusted_paths",
        "rebuild_solution", "start_background_task", "get_task_status", "list_running_tasks", "find_breaking_changes",
    ];

    /// <summary>The one restricted envelope that may hold a write tool, and the one tool it may hold.</summary>
    private static readonly (string Agent, string Tool) ImplementerApply = (Implementer, "roslyn__apply_code_action");

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
    public void Every_tool_of_the_pinned_server_is_enumerated_or_bound_to_a_policy(string appSettings, string mcpConfig)
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

    /// <summary>
    ///     The classification above names real tools of the pinned server, so neither list can go stale unnoticed. Red:
    ///     misspell one entry, or remove a tool the classification names from the snapshot.
    /// </summary>
    [Fact]
    public void The_write_and_operator_classification_names_only_tools_of_the_pinned_server()
    {
        var tools = LoadSnapshot().Tools;

        WriteTools.Concat(OperatorTools).Should().OnlyHaveUniqueItems().And.BeSubsetOf(tools);
    }

    /// <summary>
    ///     Fix round 2: a write or operator tool is guarded by its binding, whatever the envelopes say. The test above
    ///     counts any exact name in any envelope as guarded, so on its own it would pass a Cli scout that names
    ///     <c>roslyn__change_signature</c> with the Cli binding removed. Red: exactly that mutation.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public void Every_write_and_operator_tool_is_bound_whatever_the_envelopes_say(string appSettings, string mcpConfig)
    {
        _ = mcpConfig;
        var bindings = Bindings(Load(appSettings));

        WriteTools.Concat(OperatorTools)
            .Select(tool => $"{Source}__{tool}")
            .Where(tool => !bindings.Exists(pattern => Glob.IsMatch(pattern, tool)))
            .Should().BeEmpty($"{appSettings} must bind every write and operator tool in Thalos:ToolPolicies");
    }

    /// <summary>
    ///     Fix round 2: no restricted envelope offers a write or operator tool, except <c>apply_code_action</c> on the
    ///     implementer, whose calls the <c>csharp-write</c> binding gates and audits. Red: add
    ///     <c>roslyn__change_signature</c> to the Cli scout.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public void No_envelope_but_the_architects_offers_a_write_or_operator_tool_beyond_the_implementers_apply(string appSettings, string mcpConfig)
    {
        _ = mcpConfig;
        var restricted = WriteTools.Concat(OperatorTools).Select(tool => $"{Source}__{tool}").ToList();

        foreach (var (name, envelope) in Envelopes(Load(appSettings)).Where(e => !string.Equals(e.Name, Architect, StringComparison.Ordinal)))
        {
            envelope
.Where(pattern => restricted.Exists(tool => Glob.IsMatch(pattern, tool)) && (name, pattern) != ImplementerApply).Should().BeEmpty($"{name} in {appSettings} must not be offered a tool that writes or administers the server");
        }
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
