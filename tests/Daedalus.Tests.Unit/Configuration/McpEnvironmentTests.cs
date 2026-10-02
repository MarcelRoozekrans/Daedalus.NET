using System.Text.Json;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Thalos 0.13.0 starts every stdio MCP child with a scrubbed environment and adds only the host variables a
///     server lists in <c>passEnvironment</c>. This guards that the shipped <c>.mcp.json</c> never lists a secret there.
/// </summary>
public sealed class McpEnvironmentTests
{
    private static readonly string[] Secrets =
    [
        "GITHUB_TOKEN",
        "ANTHROPIC_API_KEY",
        "Thalos__Channels__Telegram__BotToken",
        "ConnectionStrings__daedalus",
    ];

    /// <summary>
    ///     Red: add <c>"GITHUB_TOKEN"</c> to roslyn's <c>passEnvironment</c> (or to its <c>runScoped</c> block) in
    ///     src/Daedalus.Api/.mcp.json.
    /// </summary>
    [Fact]
    public void No_shipped_MCP_server_is_passed_a_secret()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Daedalus.Api.mcp.json");
        using var doc = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var servers = doc.RootElement.GetProperty("mcpServers").EnumerateObject().ToList();
        servers.Should().NotBeEmpty();

        foreach (var server in servers)
        {
            var passed = new List<string?>();
            Collect(server.Value, passed);
            passed.Should().NotIntersectWith(Secrets, $"server '{server.Name}'");
        }
    }

    private static void Collect(JsonElement server, List<string?> passed)
    {
        if (server.TryGetProperty("passEnvironment", out var pass))
        {
            passed.AddRange(pass.EnumerateArray().Select(e => e.GetString()));
        }

        if (server.TryGetProperty("runScoped", out var scoped))
        {
            Collect(scoped, passed);
        }
    }
}
