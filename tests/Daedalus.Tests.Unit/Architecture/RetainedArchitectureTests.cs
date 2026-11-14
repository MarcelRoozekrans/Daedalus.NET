using Rag.NET.Abstractions;
using Thalos.Memory.RagNet;

namespace Daedalus.Tests.Unit.Architecture;

/// <summary>
///     The architecture facts the Ariadne pack in <c>.architecture/rules.yaml</c> cannot hold. Daedalus reaches
///     Rag.NET only through <c>Thalos.NET.Memory.RagNet</c> and no Daedalus code names a <c>Rag.**</c> type, so the
///     pack's <c>DAE-RAGNET-ONLY-IN-AGENTS</c> rule cannot use <c>requireTarget</c>: its target never appears in the
///     solution. These checks keep that rule load-bearing from the outside.
/// </summary>
public sealed class RetainedArchitectureTests
{
    private const string RagNetNamespacePrefix = "Rag.";

    /// <summary>
    ///     Rag.NET's types still live under <c>Rag.</c>, so the pack's <c>Rag.**</c> pattern still names them and
    ///     <c>DAE-RAGNET-ONLY-IN-AGENTS</c> cannot pass because the pattern went stale. Red: change
    ///     <see cref="RagNetNamespacePrefix"/> to <c>"Ragged."</c>; the namespace no longer starts with it.
    /// </summary>
    [Fact]
    public void RagNet_types_live_under_the_namespace_the_pack_forbids()
    {
        typeof(IVectorStore).Namespace.Should().StartWith(
            RagNetNamespacePrefix,
            "DAE-RAGNET-ONLY-IN-AGENTS forbids Rag.**; if Rag.NET moved namespace the rule would pass on nothing");
    }

    /// <summary>
    ///     <c>Thalos.NET.Memory.RagNet</c>, the adapter Daedalus.Agents uses, still references a Rag.NET assembly, so
    ///     Rag.NET really is in the solution's dependency graph and the forbid rule guards something. Red: change
    ///     <see cref="RagNetNamespacePrefix"/> to <c>"Ragged."</c>; no referenced assembly name starts with it.
    /// </summary>
    [Fact]
    public void MemoryRagNetAdapter_depends_on_RagNet()
    {
        var referenced = typeof(RagNetMemoryOptions).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        referenced.Should().Contain(
            name => name.StartsWith(RagNetNamespacePrefix, StringComparison.Ordinal),
            "Thalos.NET.Memory.RagNet is the Rag.NET adapter; without it DAE-RAGNET-ONLY-IN-AGENTS guards nothing");
    }
}
