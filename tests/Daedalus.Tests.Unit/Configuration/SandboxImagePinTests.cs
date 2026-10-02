using System.Text.Json;
using System.Text.RegularExpressions;

namespace Daedalus.Tests.Unit.Configuration;

public sealed partial class SandboxImagePinTests
{
    /// <summary>
    /// Run tool schemas come from the host Roslyn server (plan F2), so the sandbox image must run the same RoslynCodeLens
    /// version or a tool's schema could drift from the server that serves it. Both files are linked into this test's
    /// output, as the other configuration tests do. Red: change the Dockerfile's --version, or the Api's .mcp.json pin.
    /// </summary>
    [Fact]
    public void The_sandbox_image_pins_the_same_RoslynCodeLens_version_as_the_host_server()
    {
        var dockerfile = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Daedalus.Sandbox.Dockerfile"));
        var imageVersion = ToolVersion().Match(dockerfile).Groups["v"].Value;

        using var mcp = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Daedalus.Api.mcp.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var hostArg = mcp.RootElement.GetProperty("mcpServers").GetProperty("roslyn").GetProperty("args")[0].GetString()!;
        var hostVersion = hostArg[(hostArg.IndexOf('@', StringComparison.Ordinal) + 1)..];

        imageVersion.Should().NotBeEmpty().And.Be(hostVersion);
    }

    [GeneratedRegex(@"RoslynCodeLens\.Mcp\s+--version\s+(?<v>[0-9][0-9.]*)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ToolVersion();
}
