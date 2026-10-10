using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>Phase 2.8: the payload of a <c>node-usage</c> record, read back by cost analytics.</summary>
public sealed class NodeUsageTests
{
    /// <summary>Red: write <c>cacheReadTokens</c> from <c>CacheWriteTokens</c>; the round trip differs.</summary>
    [Fact]
    public void A_payload_reads_back_as_the_usage_it_was_written_from()
    {
        var usage = new NodeUsage("claude-x", 1_200, 80, 900, 150);

        NodeUsage.FromPayloadJson(usage.ToPayloadJson()).Value.Should().Be(usage);
    }

    /// <summary>Red: write the model as <c>""</c> when it is null; it reads back as <c>""</c>, not null.</summary>
    [Fact]
    public void A_usage_without_a_model_keeps_its_model_null()
    {
        NodeUsage.FromPayloadJson(new NodeUsage(null, 1, 2, 0, 0).ToPayloadJson()).Value.Model.Should().BeNull();
    }

    /// <summary>
    ///     A payload with a missing or negative count is refused, never read as zero.
    ///     Red: default a missing count to 0; the missing row passes.
    ///     Red: drop the <c>value &gt;= 0</c> check; the negative row passes.
    /// </summary>
    [Theory]
    [InlineData("""{"model":"m","outputTokens":1,"cacheReadTokens":0,"cacheWriteTokens":0}""")]
    [InlineData("""{"model":"m","inputTokens":-1,"outputTokens":1,"cacheReadTokens":0,"cacheWriteTokens":0}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public void A_malformed_payload_is_a_failure(string payload) =>
        NodeUsage.FromPayloadJson(payload).IsFailure.Should().BeTrue();
}
