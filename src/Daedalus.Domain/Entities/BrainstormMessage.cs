using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     A single message in a brainstorming conversation.
/// </summary>
public sealed class BrainstormMessage
{
    /// <summary>Gets the unique identifier for this message.</summary>
    public Guid Id { get; private set; }

    /// <summary>Gets the brainstorm session this message belongs to.</summary>
    public Guid BrainstormSessionId { get; internal set; }

    /// <summary>Gets the role of the message author (System, Assistant, or User).</summary>
    public MessageRole Role { get; private set; }

    /// <summary>Gets the text content of the message.</summary>
    public string Content { get; private set; } = string.Empty;

    /// <summary>Gets the brainstorm phase during which this message was created.</summary>
    public BrainstormPhase Phase { get; private set; }

    /// <summary>Gets when this message was created.</summary>
    public DateTime CreatedAt { get; private set; }

    private BrainstormMessage() { } // EF Core

    /// <summary>
    ///     Creates a new brainstorm message with validated parameters.
    /// </summary>
    /// <param name="role">The role of the message author.</param>
    /// <param name="content">The text content of the message.</param>
    /// <param name="phase">The brainstorm phase during which this message was created.</param>
    /// <returns>A Result containing the message or an error.</returns>
    public static Result<BrainstormMessage> Create(
        MessageRole role,
        string content,
        BrainstormPhase phase)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Result.Failure<BrainstormMessage>("Content cannot be empty.");

        return Result.Success(new BrainstormMessage
        {
            Id = Guid.NewGuid(),
            Role = role,
            Content = content,
            Phase = phase,
            CreatedAt = DateTime.UtcNow
        });
    }
}
