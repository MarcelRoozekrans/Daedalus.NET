using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>One parsed learning (output of <c>LearningsService.ParseRawLearnings</c>), not yet persisted.</summary>
/// <param name="Category">How the raw line was classified.</param>
/// <param name="Pattern">What happened (the problem, decision or observation).</param>
/// <param name="Resolution">What to do about it; equal to <paramref name="Pattern"/> for single-line learnings.</param>
/// <param name="Tags">Technology keywords extracted from the line.</param>
/// <param name="Severity">How much the learning matters.</param>
public sealed record ParsedLearning(
    LearningCategory Category,
    string Pattern,
    string Resolution,
    IReadOnlyList<string> Tags,
    LearningSeverity Severity);

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
///     Ralph's door to the agent memory (Thalos <c>IMemoryService</c> behind the adapter in <c>Daedalus.Agents</c>). Learnings
///     are written under the shared owner and recalled by semantic search. Application stays Thalos-free.
/// </summary>
public interface ILearningsMemory
{
    /// <summary>Stores one learning under the shared owner (kind <c>learning</c>, source <c>ralph:task/{sourceTaskId}</c>); returns the memory id.</summary>
    /// <param name="learning">The parsed learning to remember.</param>
    /// <param name="sourceTaskId">The task that produced the learning; becomes the memory source.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<string>> RememberAsync(ParsedLearning learning, Guid sourceTaskId, CancellationToken ct);

    /// <summary>Recalls up to <paramref name="maxResults"/> shared learnings relevant to <paramref name="query"/>, best first.</summary>
    /// <param name="query">Natural-language query (typically the task prompt).</param>
    /// <param name="maxResults">Upper bound on the number of learnings returned.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<RecalledLearning>>> RecallAsync(string query, int maxResults, CancellationToken ct);
}
