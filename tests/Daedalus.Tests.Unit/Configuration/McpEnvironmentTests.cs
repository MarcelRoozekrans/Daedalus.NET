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
    ///     Red: add <c>"GITHUB_TOKEN"</c> to roslyn's <c>passEnvironment</c>, or <c>"env": {"X": "${GITHUB_TOKEN}"}</c>,
    ///     or the same on its <c>runScoped</c> block, in either shipped .mcp.json.
    /// </summary>
    [Theory]
    [InlineData("Daedalus.Api.mcp.json")]
    [InlineData("Daedalus.Cli.mcp.json")]
    public void No_shipped_MCP_server_is_passed_a_secret(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        using var doc = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var servers = doc.RootElement.GetProperty("mcpServers").EnumerateObject().ToList();
        servers.Should().NotBeEmpty();

        foreach (var server in servers)
        {
            var passed = new List<string?>();
            var envTexts = new List<string>();
            Collect(server.Value, passed, envTexts);
            passed.Should().NotIntersectWith(Secrets, $"{fileName} server '{server.Name}' passEnvironment");
            envTexts.Where(v => Secrets.Any(s => v.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .Should().BeEmpty($"{fileName} server '{server.Name}' env must not name or reference a secret");
        }
    }

    private static void Collect(JsonElement server, List<string?> passed, List<string> envTexts)
    {
        if (server.TryGetProperty("passEnvironment", out var pass))
        {
            passed.AddRange(pass.EnumerateArray().Select(e => e.GetString()));
        }

        if (server.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in env.EnumerateObject())
            {
                envTexts.Add(entry.Name);
                envTexts.Add(entry.Value.ToString());
            }
        }

        if (server.TryGetProperty("runScoped", out var scoped))
        {
            Collect(scoped, passed, envTexts);
        }
    }
}
