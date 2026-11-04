using System.Text.Json.Serialization;
using Daedalus.Application.DTOs.Agents;

namespace Daedalus.Api;

/// <summary>
///     JSON serialization context for the agent session SSE stream.
/// </summary>
/// <remarks>
///     An <see cref="AgentEventDto"/> sets only the members its kind needs; the other eight are omitted from the
///     event, and clients written against that stream see no <c>"text":null</c>. <see cref="ApiJsonSerializerContext"/>
///     writes nulls like MVC does, so the events keep their own context with <c>WhenWritingNull</c>. This is the
///     options set the stream was serialized with before the endpoints moved off MVC; the bytes do not change.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(AgentEventDto))]
internal partial class AgentEventStreamJsonContext : JsonSerializerContext
{
}
