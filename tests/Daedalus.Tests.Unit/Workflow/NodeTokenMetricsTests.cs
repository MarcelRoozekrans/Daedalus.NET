using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Testing;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.9, spec Part B step 4: a completed node's tokens are counted on <c>daedalus.manufacture.tokens</c> when they
///     are spent, by kind, node and model, so a dashboard charts token spend without a database query. The backfill
///     writes records for tokens spent in the past and counts none. Each test uses a model or node name of its own.
/// </summary>
public sealed class NodeTokenMetricsTests
{
    /// <summary>
    ///     Red: drop the <c>RecordTokens</c> call from <c>RecordCompletionAsync</c>; nothing is measured.
    ///     Red: tag the kind <c>"cache-read"</c>; the set differs.
    ///     Red: tag the run id in place of the node; the node values differ.
    ///     Red: add <c>CacheReadTokens</c> to the input count; the input value is 2100.
    /// </summary>
    [Fact]
    public async Task A_live_completion_counts_each_kind_tagged_with_node_and_model()
    {
        var model = $"probe-model-{Guid.NewGuid():N}";
        using var capture = new TelemetryCapture(ManufactureTelemetry.SourceName);

        await TestNodeUsage.Recorder().RecordCompletionAsync(
            Guid.NewGuid(), 4, "implement", null, new TurnUsage(1_200, 80, model) { CacheReadTokens = 900, CacheWriteTokens = 150 }, CancellationToken.None);

        capture.MeasurementsOf(ManufactureTelemetry.TokensMetricName)
            .Where(m => model.Equals(m.Tags.GetValueOrDefault(ManufactureTelemetry.ModelTag)))
            .Select(m => (Kind: m.Tags[ManufactureTelemetry.TokenKindTag], m.Value, Node: m.Tags[ManufactureTelemetry.NodeTag]))
            .Should().BeEquivalentTo(new (object?, double, object?)[]
            {
                ("input", 1_200, "implement"),
                ("output", 80, "implement"),
                ("cache_read", 900, "implement"),
                ("cache_write", 150, "implement"),
            });
    }

    /// <summary>
    ///     The backfill's path. Red: emit in <c>RecordAsync</c>; the model's measurements appear.
    ///     The written assertion guards the test: a record that was refused would also measure nothing.
    /// </summary>
    [Fact]
    public async Task The_backfill_path_writes_a_record_and_counts_no_tokens()
    {
        var model = $"probe-model-{Guid.NewGuid():N}";
        using var capture = new TelemetryCapture(ManufactureTelemetry.SourceName);

        var written = await TestNodeUsage.Recorder().RecordAsync(
            Guid.NewGuid(), 4, "implement", null, new TurnUsage(1_200, 80, model), DateTime.UtcNow, CancellationToken.None);

        written.Should().BeTrue();
        capture.MeasurementsOf(ManufactureTelemetry.TokensMetricName)
            .Should().NotContain(m => model.Equals(m.Tags.GetValueOrDefault(ManufactureTelemetry.ModelTag)));
    }

    /// <summary>
    ///     Red: drop the <c>count &lt;= 0</c> guard; zero-count kinds are measured.
    ///     Red: always add the model tag; an empty model is tagged.
    /// </summary>
    [Fact]
    public async Task Zero_counts_are_not_measured_and_an_empty_model_is_not_tagged()
    {
        var node = $"probe-node-{Guid.NewGuid():N}";
        using var capture = new TelemetryCapture(ManufactureTelemetry.SourceName);

        await TestNodeUsage.Recorder().RecordCompletionAsync(Guid.NewGuid(), 1, node, null, new TurnUsage(10, 0, ""), CancellationToken.None);

        var measurement = capture.MeasurementsOf(ManufactureTelemetry.TokensMetricName)
            .Should().ContainSingle(m => node.Equals(m.Tags.GetValueOrDefault(ManufactureTelemetry.NodeTag))).Subject;
        measurement.Tags[ManufactureTelemetry.TokenKindTag].Should().Be("input");
        measurement.Tags.Should().NotContainKey(ManufactureTelemetry.ModelTag);
    }
}
