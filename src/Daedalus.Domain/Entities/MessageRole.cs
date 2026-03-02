namespace Daedalus.Domain.Entities;

/// <summary>
///     Role of a message in a brainstorming conversation.
/// </summary>
public enum MessageRole
{
    /// <summary>System-level instructions or context.</summary>
    System = 0,

    /// <summary>AI assistant response.</summary>
    Assistant = 1,

    /// <summary>User input or response.</summary>
    User = 2
}
