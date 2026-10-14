using System.Text.Json;
using Daedalus.Agents;
using Daedalus.Agents.Decisions;
using Microsoft.Extensions.Logging.Abstractions;
using Minos;
using Thalos.Testing;

namespace Daedalus.Tests.Unit.Decisions;

public class NodeSelectionDeciderTests
{
    private static readonly string[] Tools = ["workspace__*", "daedalus__*", "roslyn__*", "sandbox__*"];

    private static readonly RoleMenu Menu = new("default-model", ["cheap", "strong"], Tools);

    private static NodeSelectionContext Context(string intent = "do the thing", bool narrow = true, string node = "implement") =>
        new("developer", node, intent, 1, Menu, narrow);

    private static NodeSelectionDecider Decider(IDecisionClient? client, TimeSpan? timeout = null)
    {
        var config = new DecisionsConfig { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
        return new NodeSelectionDecider(client, config, NullLogger<NodeSelectionDecider>.Instance);
    }

    /// <summary>Red: return the mapper's answer without the null check; the fake-less call throws.</summary>
    [Fact]
    public async Task No_client_gives_the_defaults_with_fallback_off()
    {
        var selection = await Decider(null).DecideAsync(Context(), CancellationToken.None);

        selection.Should().Be(NodeSelectionMapper.Default(Menu, NodeSelectionDecider.Off));
        selection.Fallback.Should().Be("off");
        selection.Model.Should().Be("default-model");
        selection.ToolFilter.Should().BeNull();
    }

    /// <summary>
    ///     Red: drop the <c>CancelAfter</c>; the call waits the full 30 s and the elapsed bound fails.
    ///     Red: report the timeout as <c>error:Timeout</c>; the fallback assertion fails.
    /// </summary>
    [Fact]
    public async Task A_decision_slower_than_the_timeout_keeps_the_defaults()
    {
        using var client = new FakeDecisionClient(
            Result<DecisionResponse, DecisionError>.Failure(new DecisionError(DecisionErrorKind.Server, "never")))
        {
            Delay = TimeSpan.FromSeconds(30),
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var selection = await Decider(client, TimeSpan.FromMilliseconds(50)).DecideAsync(Context(), CancellationToken.None);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        selection.Fallback.Should().Be("timeout");
        selection.Model.Should().Be("default-model");
        selection.ToolFilter.Should().BeNull();
    }

    /// <summary>Red: let the caller's cancellation fall into the timeout branch; this stops throwing.</summary>
    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed()
    {
        using var client = new FakeDecisionClient(
            Result<DecisionResponse, DecisionError>.Failure(new DecisionError(DecisionErrorKind.Server, "never")))
        {
            Delay = TimeSpan.FromSeconds(30),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await Decider(client).DecideAsync(Context(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Red: label every failure <c>error</c> without the kind; the fallback assertion fails.</summary>
    [Theory]
    [InlineData(DecisionErrorKind.RateLimited)]
    [InlineData(DecisionErrorKind.Network)]
    [InlineData(DecisionErrorKind.Unauthorized)]
    public async Task A_decision_error_keeps_the_defaults_and_names_the_kind(DecisionErrorKind kind)
    {
        using var client = FakeDecisionClient.Failing(kind);

        var selection = await Decider(client).DecideAsync(Context(), CancellationToken.None);

        selection.Fallback.Should().Be($"error:{kind}");
        selection.Model.Should().Be("default-model");
        selection.ToolFilter.Should().BeNull();
    }

    /// <summary>
    ///     Red: return <c>Default</c> after a successful call; the model assertion fails.
    ///     Red: pass <c>narrowTools: true</c> always; the second case's filter assertion fails.
    /// </summary>
    [Fact]
    public async Task A_confident_answer_is_mapped()
    {
        using var client = FakeDecisionClient.Answering(Demand.Hard, 0.9, 0.1, 0.9, 0.9, 0.9, 0.9, 0.9);

        var selection = await Decider(client).DecideAsync(Context(), CancellationToken.None);
        var unnarrowed = await Decider(client).DecideAsync(Context(narrow: false), CancellationToken.None);

        selection.Fallback.Should().BeNull();
        selection.Model.Should().Be("strong");
        selection.ToolFilter.Should().Equal("workspace__*", "daedalus__*", "sandbox__*");
        unnarrowed.ToolFilter.Should().BeNull();
    }

    /// <summary>
    ///     Red: send the whole intent; the length assertion fails.
    ///     Red: drop <c>visit</c> from the state; the visit assertion fails.
    /// </summary>
    [Fact]
    public async Task The_state_carries_the_work_intent_and_no_more_than_4000_characters_of_it()
    {
        using var client = FakeDecisionClient.Answering(Demand.Routine, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9);
        var intent = new string('a', 3_999) + "bc" + new string('d', 500);

        await Decider(client).DecideAsync(Context(intent), CancellationToken.None);
        await Decider(client).DecideAsync(Context("short"), CancellationToken.None);

        using var short_ = JsonDocument.Parse(client.LastState!);
        short_.RootElement.GetProperty("work_intent").GetString().Should().Be("short");
        short_.RootElement.GetProperty("role").GetString().Should().Be("developer");
        short_.RootElement.GetProperty("node").GetString().Should().Be("implement");
        short_.RootElement.GetProperty("visit").GetInt32().Should().Be(1);

        await Decider(client).DecideAsync(Context(intent), CancellationToken.None);
        using var doc = JsonDocument.Parse(client.LastState!);
        doc.RootElement.GetProperty("work_intent").GetString().Should().Be(intent[..4_000]);
    }

    /// <summary>
    ///     Red: tag <c>zqx</c> task text or a tool name on the span; the content assertion fails.
    ///     Red: tag the span with the exception message of an error; same.
    ///     Red: drop the <c>thalos.node</c> tag; the tag assertion fails.
    ///     Red: add 0 instead of 1, or drop the fallback tag; the counter assertions fail.
    /// </summary>
    [Fact]
    public async Task No_span_tag_carries_task_text_or_a_tool_name()
    {
        var tools = new[] { "workspace__*", "daedalus__*", "zqx_tool__*" };
        var menu = new RoleMenu("default-model", ["cheap", "strong"], tools);
        var context = new NodeSelectionContext("developer", "implement", "zqx probe intent", 1, menu, true);
        using var answering = FakeDecisionClient.Answering(Demand.Hard, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9);
        using var failing = FakeDecisionClient.Failing(DecisionErrorKind.Server);
        using var capture = new TelemetryCapture(DecisionTelemetry.SourceName);

        await Decider(answering).DecideAsync(context, CancellationToken.None);
        await Decider(failing).DecideAsync(context, CancellationToken.None);

        var spans = capture.SpansNamed(DecisionTelemetry.SpanName).ToList();
        spans.Should().HaveCount(2);
        spans.Select(s => s.GetTagItem(DecisionTelemetry.NodeTag)).Should().OnlyContain(v => "implement".Equals(v));
        spans.Select(s => s.GetTagItem(DecisionTelemetry.FallbackTag)).Should().BeEquivalentTo(new object?[] { null, "error:Server" });
        spans.Select(s => s.GetTagItem(DecisionTelemetry.KeptFamiliesTag)).Should().NotContainNulls();
        spans.SelectMany(s => s.TagObjects).Select(t => t.Value?.ToString())
            .Should().NotContain(v => v != null && v.Contains("zqx", StringComparison.Ordinal));
        spans.SelectMany(s => s.TagObjects).Select(t => t.Key).Should().NotContain("daedalus.work_intent");

        var counted = capture.MeasurementsOf(DecisionTelemetry.CounterName).ToList();
        counted.Should().Contain(m => m.Value >= 1 && "none".Equals(m.Tags.GetValueOrDefault(DecisionTelemetry.FallbackTag)));
        counted.Should().Contain(m => m.Value >= 1 && "error:Server".Equals(m.Tags.GetValueOrDefault(DecisionTelemetry.FallbackTag)));
    }
}
