using System.Text.Json;
using Daedalus.Agents;
using Daedalus.Agents.Decisions;
using Daedalus.Agents.Workflow;
using Daedalus.Tests.Unit.Decisions;
using Microsoft.Extensions.Logging.Abstractions;
using Minos;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Phase 2.10 final review, item 3: the <c>node-selection</c> record holds the question set, each answer and its
///     confidence, as the spec requires, so the thresholds can be tuned from B9's records. <see cref="NodeSelectionRecord"/>
///     still reads a record written before the answers existed.
/// </summary>
public sealed class NodeSelectionRecordTests
{
    private static readonly RoleMenu Menu = new("default-model", ["cheap", "strong"], ["workspace__*", "daedalus__*", "roslyn__*", "sandbox__*"]);

    /// <summary>An answered decision, as the decider maps it: Hard at 0.9, roslyn 0.1, sandbox 0.8, memory to issues 0.3 to 0.6.</summary>
    private static async Task<NodeSelection> AnsweredAsync(double confidence = 0.9)
    {
        using var client = FakeDecisionClient.Answering(Demand.Hard, confidence, 0.1, 0.8, 0.3, 0.4, 0.5, 0.6);
        var decider = new NodeSelectionDecider(client, new DecisionsConfig(), NullLogger<NodeSelectionDecider>.Instance);
        return await decider.DecideAsync(new NodeSelectionContext("developer", "implement", "zqx task text", 1, Menu, true), CancellationToken.None);
    }

    /// <summary>
    ///     The payload carries the question-set version, the demand answer and its confidence, and every family's probability,
    ///     under these names. Reds, each applied once: drop <c>answers</c> from the payload, and the property is missing;
    ///     rename any of its four JSON names, and that lookup fails; map one family from another question in the mapper,
    ///     and its probability differs; write the version from a constant other than <c>NodeQuestions.Version</c>, and the
    ///     version differs.
    /// </summary>
    [Fact]
    public async Task The_record_holds_the_question_set_each_answer_and_its_confidence()
    {
        var payload = NodeSelectionRecord.Serialize(await AnsweredAsync());

        using var document = JsonDocument.Parse(payload);
        var answers = document.RootElement.GetProperty("answers");
        answers.GetProperty("question_set").GetInt32().Should().Be(NodeQuestions.Version);
        answers.GetProperty("demand").GetString().Should().Be("Hard");
        answers.GetProperty("demand_confidence").GetDouble().Should().Be(0.9);
        answers.GetProperty("families").EnumerateObject().Select(p => (p.Name, p.Value.GetDouble())).Should().Equal(
            ("roslyn", 0.1), ("sandbox", 0.8), ("memory", 0.3), ("skills", 0.4), ("context7", 0.5), ("issues", 0.6));
        payload.Should().NotContain("zqx", "the record holds answers, never the task text");
    }

    /// <summary>
    ///     An answer that was not taken is recorded too, since that is what tuning the threshold needs. Red: record answers
    ///     only for a taken decision; the low-confidence answers are missing.
    /// </summary>
    [Fact]
    public async Task An_answer_below_the_threshold_is_recorded_with_its_confidence()
    {
        var selection = await AnsweredAsync(confidence: 0.2);

        selection.Fallback.Should().Be(NodeSelectionMapper.LowConfidence);
        NodeSelectionRecord.Parse(NodeSelectionRecord.Serialize(selection))!.Answers!.DemandConfidence.Should().Be(0.2);
    }

    /// <summary>
    ///     A recorded decision reads back with its answers, so a redelivery reusing it records nothing less. Red: drop the
    ///     answers when parsing; they read back as null.
    /// </summary>
    [Fact]
    public async Task The_answers_survive_a_round_trip()
    {
        var selection = await AnsweredAsync();

        var parsed = NodeSelectionRecord.Parse(NodeSelectionRecord.Serialize(selection));

        parsed.Should().NotBeNull();
        parsed!.Answers.Should().BeEquivalentTo(selection.Answers);
        parsed.Model.Should().Be("strong");
    }

    /// <summary>
    ///     A record from before the answers existed, and a default with nothing asked, read as they did, with no answers; a
    ///     default writes no <c>answers</c> at all. Reds, each applied once: require <c>answers</c> in Parse, and the old
    ///     record no longer parses; write <c>answers</c> as null rather than leaving it out, and the default's payload has
    ///     the property; read an incomplete answers object as answers, and the third row reads answers with no demand.
    /// </summary>
    [Theory]
    [InlineData("""{"level":"Hard","model":"strong","tool_filter":null,"kept_families":4,"fallback":null,"confidence":0.9}""")]
    [InlineData("""{"level":"default","model":"default-model","tool_filter":null,"kept_families":4,"fallback":"off","confidence":null}""")]
    [InlineData("""{"level":"Hard","model":"strong","tool_filter":null,"kept_families":4,"fallback":null,"confidence":0.9,"answers":{"question_set":1}}""")]
    public void A_record_without_answers_still_parses(string payload)
    {
        var parsed = NodeSelectionRecord.Parse(payload);

        parsed.Should().NotBeNull();
        parsed!.Answers.Should().BeNull();
        using var written = JsonDocument.Parse(NodeSelectionRecord.Serialize(NodeSelectionMapper.Default(Menu, NodeSelectionDecider.Off)));
        written.RootElement.TryGetProperty("answers", out _).Should().BeFalse();
    }
}
