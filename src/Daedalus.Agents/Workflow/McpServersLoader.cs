using Thalos.Mcp;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Loads <c>Thalos:McpConfigPath</c>'s <c>.mcp.json</c> for <c>AddMcpServers</c>, so one file serves both run modes.
///     The file's <c>runScoped</c> blocks describe local mode: a private copy of the server started on the host for each
///     run, over the run's worktree. In sandbox mode each of them is rewritten to <c>runScoped.remote</c>, so a run's
///     calls go to the server inside the run's own sandbox, and the host entry stays as it is for callers with no run.
/// </summary>
/// <remarks>
///     <b>Why the rewrite is required, not a nicety.</b> Thalos's sandboxed provider refuses, when it is built at boot,
///     any run-scoped MCP server that is not remote: the host would start it for each run on the host, outside the
///     sandbox, where it evaluates the run's MSBuild files.
/// </remarks>
internal static class McpServersLoader
{
    /// <summary>
    ///     The servers in the file at <paramref name="path"/>, parsed as Thalos's own <see cref="McpConfigFile"/> parses
    ///     them, with every run-scoped entry made remote when <paramref name="sandboxEnabled"/>. A missing file is no
    ///     servers, as <c>AddMcpServersFromFile</c> treats it.
    /// </summary>
    /// <param name="path">The resolved <c>.mcp.json</c> path.</param>
    /// <param name="sandboxEnabled">Whether <c>Thalos:Workflow:Sandbox:Enabled</c> is set.</param>
    public static Dictionary<string, McpServerDefinition> Load(string path, bool sandboxEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var servers = File.Exists(path)
            ? new Dictionary<string, McpServerDefinition>(McpConfigFile.Load(path), StringComparer.Ordinal)
            : new Dictionary<string, McpServerDefinition>(StringComparer.Ordinal);
        if (sandboxEnabled)
        {
            foreach (var server in servers.Values)
            {
                if (server.RunScoped is { } local)
                {
                    // Args, env, cwd, readyTool, reload and readyWaitTimeout describe the local copy, which a remote entry
                    // does not have, and Thalos refuses a remote entry that sets them. The call timeout bounds each remote
                    // call as it bounded each local one, so it carries over.
                    server.RunScoped = new RunScopedMcpDefinition { Remote = true, CallTimeout = local.CallTimeout };
                }
            }
        }

        return servers;
    }
}
