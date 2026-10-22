using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Daedalus.Agents.Sessions;

/// <summary>
///     How <see cref="PostgresAgentSessionStore"/> writes a <see cref="ChatMessage"/> into <c>AgentMessages.ContentJson</c>
///     and reads it back, kept apart from the store so the bytes can be pinned without a database.
/// </summary>
internal static class AgentSessionJson
{
    // jsonb normalises key order on write, so the "$type" discriminator of AIContent is not guaranteed to come first
    // when read back — allow out-of-order metadata on the M.E.AI defaults (same converters/resolver otherwise).
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    /// <summary>The JSON a message is stored as.</summary>
    internal static string Serialize(ChatMessage message) => JsonSerializer.Serialize(message, Json);

    /// <summary>A stored message, or <see langword="null"/> for a JSON <c>null</c>.</summary>
    internal static ChatMessage? Deserialize(string json) => JsonSerializer.Deserialize<ChatMessage>(json, Json);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { AllowOutOfOrderMetadataProperties = true };
        options.MakeReadOnly();
        return options;
    }
}
