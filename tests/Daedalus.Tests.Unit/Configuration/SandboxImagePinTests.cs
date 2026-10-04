using System.Text.Json;
using System.Text.RegularExpressions;

namespace Daedalus.Tests.Unit.Configuration;

public sealed partial class SandboxImagePinTests
{
    /// <summary>
    /// Run tool schemas come from the host Roslyn server (plan F2), so the sandbox image must run the same RoslynCodeLens
    /// version or a tool's schema could drift from the server that serves it. The run-scoped entry is checked too: in local
    /// mode it is the run's own server, and in sandbox mode the image takes its place. Both files are linked into this
    /// test's output, as the other configuration tests do. Red: change the Dockerfile's --version, the host entry's pin,
    /// or the run-scoped entry's pin in the Api's .mcp.json.
    /// </summary>
    [Fact]
    public void The_sandbox_image_pins_the_same_RoslynCodeLens_version_as_the_host_server()
    {
        var dockerfile = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Daedalus.Sandbox.Dockerfile"));
        var imageVersion = ToolVersion().Match(dockerfile).Groups["v"].Value;

        using var mcp = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Daedalus.Api.mcp.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var roslyn = mcp.RootElement.GetProperty("mcpServers").GetProperty("roslyn");

        imageVersion.Should().NotBeEmpty();
        PinnedVersion(roslyn).Should().Be(imageVersion, "the host server must run the image's version");
        PinnedVersion(roslyn.GetProperty("runScoped")).Should().Be(imageVersion, "the run-scoped server must run the image's version");
    }

    /// <summary>
    ///     A run's Roslyn server gets the same project-load timeout in both modes. In local mode it is the Api's
    ///     <c>.mcp.json</c> <c>roslyn</c> entry's <c>env</c>; in sandbox mode McpServersLoader drops that entry's
    ///     environment, and the server is started by Thalos's sandbox host inside the image, which builds the definition
    ///     itself, passing the child no environment beyond its own list (Thalos 0.14.2, <c>RoslynProxyTools.Definition</c>,
    ///     internal, so it is read by reflection; <c>RunMcpServerRegistry</c> sets <c>InheritEnvironmentVariables</c> to
    ///     false). An <c>ENV</c> in the image's Dockerfile would therefore never reach the server, and this test reads the
    ///     value the image's host really sets. Red: change the <c>.mcp.json</c> entry's value.
    /// </summary>
    [Fact]
    public void The_sandbox_roslyn_server_gets_the_same_project_load_timeout_as_the_mcp_json_entry()
    {
        const string Key = "ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS";
        using var mcp = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Daedalus.Api.mcp.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var configured = mcp.RootElement.GetProperty("mcpServers").GetProperty("roslyn").GetProperty("env").GetProperty(Key).GetString();

        var settings = new Thalos.Sandbox.Host.SandboxSettings(
            Guid.NewGuid(), new string('t', 43), AllowAnyWriteExtension: true, new HashSet<string>(StringComparer.Ordinal), [".git/"],
            "roslyn-codelens-mcp", [], "tool:rebuild_solution", "/work");
        var definition = (Thalos.Mcp.McpServerDefinition)typeof(Thalos.Sandbox.Host.SandboxSettings).Assembly
            .GetType("Thalos.Sandbox.Host.RoslynProxyTools", throwOnError: true)!
            .GetMethod("Definition", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [settings])!;

        definition.RunScoped!.Env.Should().ContainKey(Key).WhoseValue.Should().Be(configured, "the image's host must load projects with the host entry's timeout");
    }

    /// <summary>The version after '@' in an entry's first argument, such as <c>RoslynCodeLens.Mcp@2.18.1</c>.</summary>
    private static string PinnedVersion(JsonElement entry)
    {
        var arg = entry.GetProperty("args")[0].GetString()!;
        return arg[(arg.IndexOf('@', StringComparison.Ordinal) + 1)..];
    }

    [GeneratedRegex(@"RoslynCodeLens\.Mcp\s+--version\s+(?<v>[0-9][0-9.]*)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ToolVersion();
}
