using Microsoft.Extensions.Hosting;
using Thalos;
using Thalos.Mcp;

namespace Daedalus.Agents.Security;

/// <summary>
///     Refuses to start a workflow host on which a <c>Thalos:ToolPolicies</c> pattern bound to
///     <see cref="CSharpWritePolicy"/> reaches a tool source that is not run-scoped. That policy admits a granted
///     workflow caller, and only a <see cref="RunScopedMcpToolSource"/> is guaranteed to serve such a caller from its
///     own run's server; any other source would apply the call to this host's own checkout.
/// </summary>
/// <remarks>
///     <para>
///     <b>What a pattern may reach.</b> Every <c>csharp-write</c> pattern must start with a literal
///     <c>&lt;source&gt;__</c>: a valid tool-source name with no glob character, then the separator. <c>Thalos.Tools.Glob</c>
///     lets <c>*</c> span <c>__</c>, so a pattern such as <c>*apply*</c> or <c>roslyn_*</c> could otherwise reach any
///     source. <c>AddDaedalusAgents</c> checks that at registration, together with the <c>.mcp.json</c> entries, and
///     this type checks the sources the container actually built, including any a host added outside
///     <c>.mcp.json</c>.
///     </para>
///     <para>
///     <b>Which sources that is.</b> A literal prefix <c>name__</c> matches the qualified tools of source <c>name</c>,
///     and also of a source named <c>name_</c>, whose tools are <c>name___tool</c>. Source names never contain
///     <c>__</c>, so no other source can match. A pattern whose sources are not registered at all matches no tool, as
///     on a test host whose <c>.mcp.json</c> declares no <c>roslyn</c>; that is allowed.
///     </para>
/// </remarks>
/// <param name="sources">Every tool source the container built.</param>
/// <param name="patterns">The patterns bound to <see cref="CSharpWritePolicy.PolicyName"/>.</param>
internal sealed class CSharpWriteBindingCheck(IEnumerable<IToolSource> sources, IReadOnlyList<string> patterns) : IHostedService
{
    /// <summary>
    ///     The source names a <c>csharp-write</c> pattern can reach, or <see langword="null"/> when it does not start with
    ///     a literal <c>&lt;source&gt;__</c> and so could reach any source.
    /// </summary>
    internal static IReadOnlyList<string>? SourcesReachedBy(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var separator = pattern.IndexOf("__", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var name = pattern[..separator];
        return ToolSourceName.IsValid(name) ? [name, name + "_"] : null;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">A pattern reaches a source that is not run-scoped.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var built = sources.ToList();
        foreach (var pattern in patterns)
        {
            var reached = SourcesReachedBy(pattern)
                ?? throw new InvalidOperationException(NotLiteral(pattern));
            if (built.FirstOrDefault(s => reached.Contains(s.Name, StringComparer.Ordinal) && s is not RunScopedMcpToolSource) is { } source)
            {
                throw new InvalidOperationException(
                    $"Thalos:ToolPolicies binds '{pattern}' to {CSharpWritePolicy.PolicyName}, which reaches tool source " +
                    $"'{source.Name}', and that source is not a run-scoped MCP server. The policy lets a granted workflow " +
                    "run call the tool, and only a runScoped server serves a run from its own worktree.");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The refusal for a pattern that does not start with a literal source name.</summary>
    internal static string NotLiteral(string pattern) =>
        $"Thalos:ToolPolicies binds '{pattern}' to {CSharpWritePolicy.PolicyName}, but the pattern does not start with a " +
        "literal '<source>__'. A glob can span the separator and reach any tool source, so a csharp-write pattern must " +
        "name its run-scoped server, as in 'roslyn__apply_*'.";
}
