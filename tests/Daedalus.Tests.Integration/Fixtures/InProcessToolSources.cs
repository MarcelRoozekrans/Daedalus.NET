using Thalos;
using Thalos.Mcp;
using Thalos.Tools;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     The tool sources a boundary test enumerates: every registered source but the three MCP kinds, whose enumeration
///     starts or reaches a real server. Matched by exclusion because the in-process kinds are not one public type: since
///     Thalos 0.18.0 the workspace tools are an internal generic <c>LocalToolSource&lt;T&gt;</c>, and memory and skills
///     have types of their own, so <c>OfType&lt;LocalToolSource&gt;()</c> would skip all three.
/// </summary>
internal static class InProcessToolSources
{
    /// <summary>
    ///     The in-process sources the API test host registers. A boundary test asserts its enumeration against this set,
    ///     so a source the filter drops turns it red. One entry per registration: since phase 3.2 the four daedalus
    ///     classes are four typed registrations of that one name.
    /// </summary>
    public static readonly string[] ApiHostNames =
        ["memory", "skills", "daedalus", "daedalus", "daedalus", "daedalus", "repoaction", "issues", "manufacture", "git"];

    /// <summary>The sources of <paramref name="sources"/> that are not MCP sources.</summary>
    public static IEnumerable<IToolSource> Of(IEnumerable<IToolSource> sources) =>
        sources.Where(s => s is not (McpToolSource or RunScopedMcpToolSource or RemoteRunToolSource));
}
