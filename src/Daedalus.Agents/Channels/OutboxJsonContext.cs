using System.Text.Json.Serialization;
using Daedalus.Agents.Scheduling;

namespace Daedalus.Agents.Channels;

/// <summary>
///     The source-generated System.Text.Json context for every payload on the channel outbox pipeline. ZeroAlloc's
///     generated <c>AddSerializerDispatcher</c> binds each <c>[ZeroAllocSerializable]</c> payload to this context,
///     so the outbox serializes without reflection and stays Native AOT safe.
/// </summary>
/// <remarks>
///     No <see cref="JsonSourceGenerationOptionsAttribute"/>: the rows already in <c>"OutboxMessages"</c> were
///     written by <c>WithSystemTextJsonSerializer</c> with default <c>JsonSerializerOptions</c> (PascalCase names,
///     no indentation), and a context with default options writes and reads exactly those bytes. A new payload
///     type needs a <c>[JsonSerializable]</c> line here and <c>[ZeroAllocSerializable]</c> on the type itself;
///     <c>OutboxJsonContextTests</c> fails for a type that has no line.
/// </remarks>
[JsonSerializable(typeof(ChannelMessageQueued))]
[JsonSerializable(typeof(ScheduledRunDue))]
[JsonSerializable(typeof(RunScoutStep))]
[JsonSerializable(typeof(RunWriterStep))]
[JsonSerializable(typeof(DeliverDigest))]
internal sealed partial class OutboxJsonContext : JsonSerializerContext;
