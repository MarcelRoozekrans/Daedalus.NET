using Thalos;
using Thalos.Memory;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Playwright.Browser.Fixtures;

/// <summary>
///     Vector-free <see cref="IMemoryIndex"/> for browser tests: the E2E host has no Ollama, so the real Rag.NET index would
///     report itself unavailable and every seeded memory would stay <c>index_pending</c>. This stub reports a healthy
///     768-dimension index and accepts every write; recall is not exercised through it — <see cref="StubAgentRuntime"/>
///     emits the <c>memory-recalled</c> event itself, straight off the store.
/// </summary>
internal sealed class StubMemoryIndex : IMemoryIndex
{
    /// <summary>Dimensions the stub reports (nomic-embed-text, like production).</summary>
    public const int Dimensions = 768;

    public ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<MemoryRecord> records, CancellationToken ct) =>
        new(UnitResult<AgentError>.Success());

    public ValueTask<Result<IReadOnlyList<MemoryHit>, AgentError>> SearchAsync(string query, MemoryScope scope, MemorySearchOptions options, CancellationToken ct) =>
        new(Result<IReadOnlyList<MemoryHit>, AgentError>.Success([]));

    public ValueTask<UnitResult<AgentError>> RemoveAsync(MemoryId id, CancellationToken ct) =>
        new(UnitResult<AgentError>.Success());

    public ValueTask<Result<MemoryIndexHealth, AgentError>> ProbeAsync(CancellationToken ct) =>
        new(Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(Available: true, Dimensions)));
}
