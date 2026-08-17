using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     One Microsoft.Extensions.AI ChatMessage of an <see cref="AgentSession"/>, stored as JSON
///     (<see cref="ContentJson"/>) so tool-call/result content round-trips exactly.
///     <see cref="Role"/> and the token columns are denormalised for querying and cost reporting.
/// </summary>
public sealed class AgentMessage : Entity<Guid>
{
    /// <summary>Gets the owning session id.</summary>
    public Guid SessionId { get; private set; }

    /// <summary>Gets the zero-based append order within the session.</summary>
    public int Sequence { get; private set; }

    /// <summary>Gets the chat role (user, assistant, tool, system).</summary>
    public string Role { get; private set; } = string.Empty;

    /// <summary>Gets the serialized ChatMessage (jsonb).</summary>
    public string ContentJson { get; private set; } = string.Empty;

    /// <summary>Gets the input tokens attributed to this message, if known.</summary>
    public int? InputTokens { get; private set; }

    /// <summary>Gets the output tokens attributed to this message, if known.</summary>
    public int? OutputTokens { get; private set; }

    /// <summary>Gets the model id that produced this message, if known.</summary>
    public string? ModelId { get; private set; }

    /// <summary>Gets when the message was stored (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    private AgentMessage() { } // EF Core

    /// <summary>Creates a new message with validated parameters.</summary>
    /// <param name="sessionId">The owning session id.</param>
    /// <param name="sequence">The zero-based append order within the session.</param>
    /// <param name="role">The chat role.</param>
    /// <param name="contentJson">The serialized ChatMessage.</param>
    /// <param name="inputTokens">Input tokens attributed to this message, if known.</param>
    /// <param name="outputTokens">Output tokens attributed to this message, if known.</param>
    /// <param name="modelId">The producing model id, if known.</param>
    /// <param name="utcNow">The creation timestamp (UTC).</param>
    /// <returns>A Result containing the message or an error.</returns>
    public static Result<AgentMessage> Create(
        Guid sessionId,
        int sequence,
        string role,
        string contentJson,
        int? inputTokens,
        int? outputTokens,
        string? modelId,
        DateTime utcNow)
    {
        if (sessionId == Guid.Empty)
            return Result.Failure<AgentMessage>("Session id is required.");

        if (sequence < 0)
            return Result.Failure<AgentMessage>("Sequence must be non-negative.");

        if (string.IsNullOrWhiteSpace(role))
            return Result.Failure<AgentMessage>("Role is required.");

        if (string.IsNullOrWhiteSpace(contentJson))
            return Result.Failure<AgentMessage>("Content is required.");

        return Result.Success(new AgentMessage
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Sequence = sequence,
            Role = role,
            ContentJson = contentJson,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ModelId = modelId,
            CreatedAt = utcNow,
        });
    }
}
