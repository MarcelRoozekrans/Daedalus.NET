using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Memory;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Memory;

/// <summary>
///     <see cref="ILearningsMemory"/> over Thalos' <see cref="IMemoryService"/>: learnings are host-written project
///     knowledge, so they live under the shared owner, agent-less, kind <c>learning</c>. Thalos' <c>Result</c> with an
///     <c>AgentError</c> is mapped to a ZeroAlloc <c>Result</c> with a string error at this boundary, so Application stays Thalos-free.
/// </summary>
/// <param name="memory">The Thalos memory facade (store + index).</param>
/// <param name="memoryOptions">
///     Thalos' own memory options — the shared owner is read from here rather than from <see cref="MemoryConfig"/> so this
///     adapter and Thalos' auto-recall always agree on the owner string (Thalos normalises a blank owner to null).
/// </param>
/// <param name="recall">The learnings recall budget (<c>Thalos:Memory:LearningsRecall</c>).</param>
/// <param name="logger">Logger.</param>
public sealed partial class ThalosLearningsMemory(
    IMemoryService memory,
    IOptions<MemoryOptions> memoryOptions,
    LearningsRecallConfiguration recall,
    ILogger<ThalosLearningsMemory> logger) : ILearningsMemory
{
    private string? SharedOwnerId => memoryOptions.Value.SharedOwnerId;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RecalledLearning>>> RecallAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Result<IReadOnlyList<RecalledLearning>>.Success([]);
        }

        if (SharedOwnerId is not { Length: > 0 } owner)
        {
            LogNoSharedOwner(logger);
            return Result<IReadOnlyList<RecalledLearning>>.Failure("No shared memory owner is configured; nothing was recalled.");
        }

        // Owner-wide shared scope: no agent pin, no second (shared) owner — the learnings *are* the shared owner's memories.
        var scope = new MemoryScope(owner, null, null);
        var options = new RecallOptions
        {
            TopK = Math.Clamp(maxResults, LearningsRecallConfiguration.MinTopK, LearningsRecallConfiguration.MaxTopK),
            MinScore = recall.MinScore,
            MaxChars = 0, // no character budget — TopK caps the count, and the caller caps what it renders
        };

        var result = await memory.RecallAsync(query, scope, options, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            LogRecallFailed(logger, result.Error.Code, result.Error.Message);
            return Result<IReadOnlyList<RecalledLearning>>.Failure($"{result.Error.Code}: {result.Error.Message}");
        }

        IReadOnlyList<RecalledLearning> learnings = [.. result.Value.Memories
            .Select(h => new RecalledLearning(h.Record.Id.ToString(), h.Record.Text, h.Record.Tags, h.Score, h.Record.CreatedAt))];
        return Result<IReadOnlyList<RecalledLearning>>.Success(learnings);
    }

    [LoggerMessage(EventId = 501, Level = LogLevel.Debug, Message = "Recalling learnings failed: {Code} {Message}")]
    private static partial void LogRecallFailed(ILogger logger, AgentErrorCode code, string message);

    [LoggerMessage(EventId = 502, Level = LogLevel.Warning, Message = "Thalos:Memory:SharedOwnerId resolved to nothing; learnings are disabled")]
    private static partial void LogNoSharedOwner(ILogger logger);
}
