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

    /// <summary>The version after '@' in an entry's first argument, such as <c>RoslynCodeLens.Mcp@2.18.1</c>.</summary>
    private static string PinnedVersion(JsonElement entry)
    {
        var arg = entry.GetProperty("args")[0].GetString()!;
        return arg[(arg.IndexOf('@', StringComparison.Ordinal) + 1)..];
    }

    [GeneratedRegex(@"RoslynCodeLens\.Mcp\s+--version\s+(?<v>[0-9][0-9.]*)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ToolVersion();
}
