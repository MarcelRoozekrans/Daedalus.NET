using ZeroAlloc.Results;

namespace Daedalus.Application.Abstractions;

/// <summary>A learning recalled from memory for a prompt.</summary>
/// <param name="Id">The memory id (opaque to the Application layer).</param>
/// <param name="Text">The remembered text (pattern and resolution separated by a newline).</param>
/// <param name="Tags">Category, severity and the free tags the learning was stored with.</param>
/// <param name="Score">Cosine similarity in [0,1] — higher is more relevant.</param>
/// <param name="CreatedAt">When the learning was first remembered.</param>
public sealed record RecalledLearning(
    string Id,
    string Text,
    IReadOnlyList<string> Tags,
    double Score,
    DateTimeOffset CreatedAt);

/// <summary>
///     The read-only door to the shared learnings in the agent memory (Thalos <c>IMemoryService</c> behind the adapter in
///     <c>Daedalus.Agents</c>). Learnings are recalled by semantic search. Application stays Thalos-free.
/// </summary>
public interface ILearningsMemory
{
    /// <summary>Recalls up to <paramref name="maxResults"/> shared learnings relevant to <paramref name="query"/>, best first.</summary>
    /// <param name="query">Natural-language query (typically the task prompt).</param>
    /// <param name="maxResults">Upper bound on the number of learnings returned.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<RecalledLearning>>> RecallAsync(string query, int maxResults, CancellationToken ct);
}
