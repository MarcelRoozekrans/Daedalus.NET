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

    /// <summary>Red: drop the null check; `client!` is then needed to compile (nullable analysis refuses otherwise) and the null client throws NullReferenceException.</summary>
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

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
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

    /// <summary>
    ///     Final review item 5, superseding B3's <c>InvalidOperationException</c>-only catch: any exception the client throws,
    ///     other than the caller's own cancellation, keeps the defaults as <c>error:Client</c>, and the log names the type,
    ///     never the message. Reds, each applied once: narrow the catch back to <c>InvalidOperationException</c>, and the
    ///     <c>HttpRequestException</c>, <c>FormatException</c> and <c>KeyNotFoundException</c> rows throw; use another
    ///     reason than <c>ClientFault</c>, and the fallback assertions fail; log the message, and the log carries <c>zqx</c>;
    ///     drop the log, and no line names the type.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClientFaults))]
    public async Task A_client_that_throws_keeps_the_defaults(Exception fault)
    {
        using var client = new FakeDecisionClient(
            Result<DecisionResponse, DecisionError>.Failure(new DecisionError(DecisionErrorKind.Server, "never")))
        {
            Throws = fault,
        };
        var log = new ListLogger();

        var selection = await new NodeSelectionDecider(client, new DecisionsConfig(), log).DecideAsync(Context(), CancellationToken.None);

        selection.Fallback.Should().Be("error:Client");
        selection.Fallback.Should().Be(NodeSelectionDecider.ClientFault);
        selection.Model.Should().Be("default-model");
        selection.ToolFilter.Should().BeNull();
        log.Lines.Should().Contain(l => l.Contains(fault.GetType().Name, StringComparison.Ordinal));
        log.Lines.Should().NotContain(l => l.Contains("zqx", StringComparison.Ordinal));
    }

    public static TheoryData<Exception> ClientFaults() =>
        new()
        {
            new InvalidOperationException("zqx different question set"),
            new ObjectDisposedException("client", "zqx disposed"),
            new HttpRequestException("zqx connection refused"),
            new FormatException("zqx a bug"),
            new KeyNotFoundException("zqx bad state"),
        };

    /// <summary>Collects every formatted log line.</summary>
    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<NodeSelectionDecider>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    /// <summary>
    ///     A client that throws the caller's own cancellation is not a client fault: it propagates. Red: catch every exception
    ///     in the client call, cancellations included; the call returns <c>error:Client</c> instead of throwing.
    /// </summary>
    [Fact]
    public async Task A_client_that_throws_the_callers_cancellation_propagates_it()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var client = new FakeDecisionClient(
            Result<DecisionResponse, DecisionError>.Failure(new DecisionError(DecisionErrorKind.Server, "never")))
        {
            Throws = new OperationCanceledException(cts.Token),
        };

        var act = async () => await Decider(client).DecideAsync(Context(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    ///     Final review item 7: the chosen model is tagged <c>daedalus.decision.model</c>, not the GenAI request attribute,
    ///     which names the model an inference span calls. Red: restore <c>gen_ai.request.model</c> as the tag's name; both
    ///     assertions fail.
    /// </summary>
    [Fact]
    public async Task The_span_tags_the_chosen_model_as_a_decision_attribute()
    {
        using var client = FakeDecisionClient.Answering(Demand.Hard, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9);
        using var capture = new TelemetryCapture(DecisionTelemetry.SourceName);

        await Decider(client).DecideAsync(Context(node: "model-tag"), CancellationToken.None);

        var span = capture.SpansNamed(DecisionTelemetry.SpanName).Single(s => "model-tag".Equals(s.GetTagItem(DecisionTelemetry.NodeTag)));
        span.GetTagItem("daedalus.decision.model").Should().Be("strong");
        span.GetTagItem("gen_ai.request.model").Should().BeNull();
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
    ///     Red: cut with <c>[..MaxIntentCharacters]</c> only; the emoji is split and the length and lone surrogate assertions fail.
    /// </summary>
    [Fact]
    public async Task The_cut_never_splits_a_surrogate_pair()
    {
        using var client = FakeDecisionClient.Answering(Demand.Routine, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9);
        var intent = new string('a', 3_999) + "\U0001F600" + new string('d', 10);

        await Decider(client).DecideAsync(Context(intent), CancellationToken.None);

        using var doc = JsonDocument.Parse(client.LastState!);
        var sent = doc.RootElement.GetProperty("work_intent").GetString()!;
        sent.Should().HaveLength(3_999);
        sent.Should().Be(new string('a', 3_999));
        sent.Any(char.IsSurrogate).Should().BeFalse();
    }

    /// <summary>
    ///     Red: drop the confidence tag; the answered spans lose it and the assertions fail.
    ///     Red: tag the confidence on error spans too; the absent assertion fails.
    /// </summary>
    [Fact]
    public async Task The_span_carries_the_demand_confidence_only_when_minos_answered()
    {
        using var taken = FakeDecisionClient.Answering(Demand.Hard, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9);
        using var unsure = FakeDecisionClient.Answering(Demand.Hard, 0.01, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9);
        using var failing = FakeDecisionClient.Failing(DecisionErrorKind.Server);
        using var capture = new TelemetryCapture(DecisionTelemetry.SourceName);

        await Decider(taken).DecideAsync(Context(node: "taken"), CancellationToken.None);
        await Decider(unsure).DecideAsync(Context(node: "unsure"), CancellationToken.None);
        await Decider(failing).DecideAsync(Context(node: "failing"), CancellationToken.None);
        await Decider(null).DecideAsync(Context(node: "off"), CancellationToken.None);

        var spans = capture.SpansNamed(DecisionTelemetry.SpanName).ToDictionary(s => (string)s.GetTagItem(DecisionTelemetry.NodeTag)!, StringComparer.Ordinal);
        spans["taken"].GetTagItem(DecisionTelemetry.ConfidenceTag).Should().Be(0.9);
        spans["unsure"].GetTagItem(DecisionTelemetry.ConfidenceTag).Should().Be(0.01);
        spans["unsure"].GetTagItem(DecisionTelemetry.FallbackTag).Should().Be("low-confidence");
        spans["failing"].GetTagItem(DecisionTelemetry.ConfidenceTag).Should().BeNull();
        spans["off"].GetTagItem(DecisionTelemetry.ConfidenceTag).Should().BeNull();
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
