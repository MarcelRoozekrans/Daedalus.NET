using System.Reflection;
using System.Text.Json;
using Daedalus.Agents;
using Daedalus.Agents.Channels;
using Daedalus.Agents.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox;
using ZeroAlloc.Serialisation;

namespace Daedalus.Tests.Unit.Agents.Channels;

/// <summary>
///     Pins <see cref="OutboxJsonContext"/>, the source-generated serializer context behind the channel outbox
///     pipeline. Rows already in <c>"OutboxMessages"</c> were written by the reflection serializer, so the context
///     must read those bytes and write the same ones.
/// </summary>
public sealed class OutboxJsonContextTests
{
    private static readonly Guid Id = new Guid(0x3f2504e0, 0x4f89, 0x11d3, 0x9a, 0xc, 0x3, 0x5, 0xe8, 0x2c, 0x33, 0x1) /* 3f2504e0-4f89-11d3-9a0c-0305e82c3301 */;

    // One sample per payload type; the null-and-non-null shapes of the optional field are both covered.
    public static TheoryData<object> Payloads() =>
    [
        new ChannelMessageQueued("telegram", "conv-1", "hello \"world\" é", Id),
        new ChannelMessageQueued("console", "conv-2", "no execution"),
        new ScheduledRunDue(Id, new DateTime(2026, 11, 11, 8, 30, 0, DateTimeKind.Utc)),
        new RunScoutStep(Id),
        new RunWriterStep(Id),
        new DeliverDigest(Id),
    ];

    private static IReadOnlyList<Type> RegisteredPayloadTypes() =>
        typeof(OutboxJsonContext).Assembly.GetTypes()
            .Where(t => Attribute.IsDefined(t, typeof(OutboxMessageAttribute)))
            .ToList();

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Every_registered_outbox_payload_round_trips_through_the_context(object payload)
    {
        var type = payload.GetType();

        var json = JsonSerializer.Serialize(payload, type, OutboxJsonContext.Default);
        var back = JsonSerializer.Deserialize(json, type, OutboxJsonContext.Default);

        back.Should().Be(payload);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Legacy_reflection_bytes_deserialize_through_the_context(object payload)
    {
        var type = payload.GetType();
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(payload, type, new JsonSerializerOptions());

        var back = JsonSerializer.Deserialize(legacyBytes, type, OutboxJsonContext.Default);

        back.Should().Be(payload);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void The_context_writes_the_same_bytes_as_the_reflection_serializer(object payload)
    {
        var type = payload.GetType();
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(payload, type, new JsonSerializerOptions());

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, type, OutboxJsonContext.Default);

        bytes.Should().Equal(legacyBytes);
    }

    [Fact]
    public void Context_has_no_missing_payload_type()
    {
        var types = RegisteredPayloadTypes();

        types.Should().HaveCount(5, "the five outbox registrations each have one payload type");
        foreach (var type in types)
        {
            OutboxJsonContext.Default.GetTypeInfo(type).Should().NotBeNull($"{type.Name} needs a [JsonSerializable] line");
            Attribute.IsDefined(type, typeof(ZeroAllocSerializableAttribute))
                .Should().BeTrue($"{type.Name} needs [ZeroAllocSerializable] to reach the generated dispatcher");
        }
    }

    [Fact]
    public void Every_registered_outbox_payload_type_has_a_sample()
    {
        var sampled = Enumerable.Select<object[], Type>(Payloads(), row => row[0].GetType()).ToHashSet();

        RegisteredPayloadTypes().Should().BeSubsetOf(sampled, "a new payload type needs a sample in Payloads");
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void The_serializer_dispatcher_the_outbox_uses_round_trips_and_reads_legacy_bytes(object payload)
    {
        var type = payload.GetType();
        using var provider = new ServiceCollection().AddSerializerDispatcher().BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ISerializerDispatcher>();
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(payload, type, new JsonSerializerOptions());

        dispatcher.Serialize(payload, type).ToArray().Should().Equal(legacyBytes);
        dispatcher.Deserialize(legacyBytes, type).Should().Be(payload);
    }
}
