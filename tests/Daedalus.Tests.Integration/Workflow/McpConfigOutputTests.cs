namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Task B9 fix round 2: this output's <c>.mcp.json</c> must be Daedalus.Api's. It arrives through the Api project
///     reference, and any other referenced host whose <c>.mcp.json</c> is a copy-to-output item would land at the same
///     path, the newer file winning. Daedalus.Cli's once did, and every test reading <c>.mcp.json</c> then read the Cli's,
///     which declares no run-scoped server. The Api's file is also linked as <c>Daedalus.Api.mcp.json</c>, so the two can
///     be compared.
/// </summary>
public sealed class McpConfigOutputTests
{
    /// <summary>
    ///     Red: give Daedalus.Cli's <c>.mcp.json</c> a copy-to-output item again and make it the newer file; the output's
    ///     <c>.mcp.json</c> is then the Cli's.
    /// </summary>
    [Fact]
    public void The_output_mcp_json_is_the_api_file()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ".mcp.json"));
        var api = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Daedalus.Api.mcp.json"));

        output.Should().Be(api, "the tests read the Api host's MCP configuration, never another host's");
    }
}
