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

    private static readonly NodeUsage Total = new("decided", 3_000, 300, 120, 30);

    private static readonly NodeUsage[] TwoModels =
    [
        new("decided", 2_000, 200, 100, 20),
        new("pinned", 1_000, 100, 20, 10),
    ];

    /// <summary>
    ///     Phase 2.10: a node that ran on two models reads back with both parts, in order, and the totals as written.
    ///     Red: drop the <c>byModel</c> array from <see cref="NodeUsage.ToPayloadJson"/>; the read-back has no split.
    ///     Red: write each part's <c>cacheReadTokens</c> from the total; the split no longer sums and the read fails.
    /// </summary>
    [Fact]
    public void A_split_reads_back_part_by_part()
    {
        var usage = Total.WithSplit(TwoModels).Value;

        var read = NodeUsage.FromPayloadJson(usage.ToPayloadJson());

        read.IsSuccess.Should().BeTrue(read.IsFailure ? read.Error : "");
        read.Value.Parts.Should().Equal(TwoModels);
        read.Value.ByModel.Should().NotBeNull();
        read.Value.Model.Should().Be("decided");
        read.Value.InputTokens.Should().Be(3_000);
    }

    /// <summary>
    ///     A record written before the split existed, or for a node that ran on one model, is one part: itself.
    ///     Red: return an empty list for <see cref="NodeUsage.Parts"/> when there is no split; nothing is priced.
    /// </summary>
    [Fact]
    public void A_payload_without_a_split_is_one_part()
    {
        var read = NodeUsage.FromPayloadJson("""{"model":"m","inputTokens":5,"outputTokens":1,"cacheReadTokens":0,"cacheWriteTokens":0}""").Value;

        read.ByModel.Should().BeNull();
        read.Parts.Should().Equal(new NodeUsage("m", 5, 1, 0, 0));
    }

    /// <summary>
    ///     A split must sum to the totals, so a node is never priced from parts that disagree with its own total.
    ///     Red: drop the sum check in <see cref="NodeUsage.WithSplit"/>; the off-by-one rows succeed.
    ///     Red: drop the at-least-two check; the single-part row succeeds.
    /// </summary>
    [Theory]
    [InlineData(1, 0, 0, 0, 2)]
    [InlineData(0, 1, 0, 0, 2)]
    [InlineData(0, 0, 1, 0, 2)]
    [InlineData(0, 0, 0, 1, 2)]
    [InlineData(0, 0, 0, 0, 1)]
    public void A_split_that_does_not_sum_to_the_totals_is_refused(int input, int output, int cacheRead, int cacheWrite, int parts)
    {
        NodeUsage[] split = parts == 1
            ? [Total]
            : [TwoModels[0] with { InputTokens = 2_000 + input, OutputTokens = 200 + output, CacheReadTokens = 100 + cacheRead, CacheWriteTokens = 20 + cacheWrite }, TwoModels[1]];

        Total.WithSplit(split).IsFailure.Should().BeTrue();
    }

    /// <summary>
    ///     A stored split that is not an array of valid parts, or does not sum, is unreadable rather than priced.
    ///     Red: ignore a <c>byModel</c> that is not an array; the first row reads as unsplit.
    ///     Red: read a missing count as zero; the second row reads, its parts summing to the totals.
    ///     Red: drop the sum check in <see cref="NodeUsage.WithSplit"/>; the third row reads.
    /// </summary>
    [Theory]
    [InlineData("""{"model":"m","inputTokens":5,"outputTokens":1,"cacheReadTokens":0,"cacheWriteTokens":0,"byModel":{}}""")]
    [InlineData("""{"model":"m","inputTokens":5,"outputTokens":1,"cacheReadTokens":0,"cacheWriteTokens":0,"byModel":[{"model":"m","inputTokens":5,"cacheReadTokens":0,"cacheWriteTokens":0},{"model":"p","inputTokens":0,"outputTokens":1,"cacheReadTokens":0,"cacheWriteTokens":0}]}""")]
    [InlineData("""{"model":"m","inputTokens":5,"outputTokens":1,"cacheReadTokens":0,"cacheWriteTokens":0,"byModel":[{"model":"m","inputTokens":5,"outputTokens":1,"cacheReadTokens":0,"cacheWriteTokens":0},{"model":"p","inputTokens":1,"outputTokens":0,"cacheReadTokens":0,"cacheWriteTokens":0}]}""")]
    public void A_malformed_split_is_a_failure(string payload) =>
        NodeUsage.FromPayloadJson(payload).IsFailure.Should().BeTrue();
}
