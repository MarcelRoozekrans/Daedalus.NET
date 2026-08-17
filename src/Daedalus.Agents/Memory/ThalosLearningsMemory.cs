using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Thalos;
using Thalos.Memory;

namespace Daedalus.Agents.Memory;

/// <summary>
///     <see cref="ILearningsMemory"/> over Thalos' <see cref="IMemoryService"/>: Ralph learnings are host-written project
///     knowledge, so they live under the shared owner, agent-less, kind <c>learning</c>. ZeroAlloc results are converted to
///     CSFE at this boundary (Application convention).
/// </summary>
/// <param name="memory">The Thalos memory facade (store + index).</param>
/// <param name="memoryOptions">
///     Thalos' own memory options — the shared owner is read from here rather than from <see cref="MemoryConfig"/> so this
///     adapter and Thalos' auto-recall always agree on the owner string (Thalos normalises a blank owner to null).
/// </param>
/// <param name="recall">The Ralph recall budget (<c>Thalos:Memory:RalphRecall</c>).</param>
/// <param name="logger">Logger.</param>
public sealed partial class ThalosLearningsMemory(
    IMemoryService memory,
    IOptions<MemoryOptions> memoryOptions,
    RalphRecallConfiguration recall,
    ILogger<ThalosLearningsMemory> logger) : ILearningsMemory
{
    private string? SharedOwnerId => memoryOptions.Value.SharedOwnerId;

    /// <inheritdoc />
    public async Task<Result<string>> RememberAsync(ParsedLearning learning, Guid sourceTaskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(learning);

        if (SharedOwnerId is not { Length: > 0 } owner)
        {
            LogNoSharedOwner(logger);
            return Result.Failure<string>("No shared memory owner is configured; the learning was not stored.");
        }

        var request = new RememberRequest
        {
            OwnerId = owner,
            AgentId = null,
            Kind = MemoryKind.Learning,
            Text = LearningMemoryMapping.Text(learning.Pattern, learning.Resolution),
            Tags = LearningMemoryMapping.Tags(learning.Category, learning.Severity, learning.Tags),
            Source = LearningMemoryMapping.Source(sourceTaskId),
            Importance = LearningMemoryMapping.Importance(learning.Severity),
        };

        var result = await memory.RememberAsync(request, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            LogRememberFailed(logger, result.Error.Code, result.Error.Message);
            return Result.Failure<string>($"{result.Error.Code}: {result.Error.Message}");
        }

        return Result.Success(result.Value.Id.ToString());
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RecalledLearning>>> RecallAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Result.Success<IReadOnlyList<RecalledLearning>>([]);
        }

        if (SharedOwnerId is not { Length: > 0 } owner)
        {
            LogNoSharedOwner(logger);
            return Result.Failure<IReadOnlyList<RecalledLearning>>("No shared memory owner is configured; nothing was recalled.");
        }

        // Owner-wide shared scope: no agent pin, no second (shared) owner — the learnings *are* the shared owner's memories.
        var scope = new MemoryScope(owner, null, null);
        var options = new RecallOptions
        {
            TopK = Math.Clamp(maxResults, RalphRecallConfiguration.MinTopK, RalphRecallConfiguration.MaxTopK),
            MinScore = recall.MinScore,
            MaxChars = 0, // no character budget — the prompt builder caps the enrichment block, TopK caps the count
        };

        var result = await memory.RecallAsync(query, scope, options, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            LogRecallFailed(logger, result.Error.Code, result.Error.Message);
            return Result.Failure<IReadOnlyList<RecalledLearning>>($"{result.Error.Code}: {result.Error.Message}");
        }

        IReadOnlyList<RecalledLearning> learnings = [.. result.Value
            .Select(h => new RecalledLearning(h.Record.Id.ToString(), h.Record.Text, h.Record.Tags, h.Score, h.Record.CreatedAt))];
        return Result.Success(learnings);
    }

    [LoggerMessage(EventId = 500, Level = LogLevel.Warning, Message = "Remembering a Ralph learning failed: {Code} {Message}")]
    private static partial void LogRememberFailed(ILogger logger, AgentErrorCode code, string message);

    [LoggerMessage(EventId = 501, Level = LogLevel.Debug, Message = "Recalling Ralph learnings failed: {Code} {Message}")]
    private static partial void LogRecallFailed(ILogger logger, AgentErrorCode code, string message);

    [LoggerMessage(EventId = 502, Level = LogLevel.Warning, Message = "Thalos:Memory:SharedOwnerId resolved to nothing; Ralph learnings are disabled")]
    private static partial void LogNoSharedOwner(ILogger logger);
}
