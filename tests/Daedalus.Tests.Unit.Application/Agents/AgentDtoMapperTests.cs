using Daedalus.Agents.Api;
using Microsoft.Extensions.AI;
using Thalos;

namespace Daedalus.Tests.Unit.Application.Agents;

public sealed class AgentDtoMapperTests
{
    private static readonly SessionId Session = SessionId.New();
    private static readonly TurnId Turn = TurnId.New();

    [Fact]
    public void ToDtos_folds_tool_results_into_the_assistant_message_and_skips_empty_messages()
    {
        var t0 = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "npgsql" };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "find learnings about npgsql") { CreatedAt = t0 },
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "daedalus__search_learnings", arguments)]) { CreatedAt = t0.AddSeconds(1) },
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "[{\"pattern\":\"timeout\"}]")]),
            new(ChatRole.Assistant, "Found one learning about timeouts.") { CreatedAt = t0.AddSeconds(2) },
            new(ChatRole.Assistant, ""),
        };

        var dtos = AgentDtoMapper.ToDtos(messages);

        dtos.Should().HaveCount(3);
        dtos[0].Should().BeEquivalentTo(new { Role = "user", Text = "find learnings about npgsql", CreatedAt = t0 });
        dtos[0].ToolCalls.Should().BeEmpty();

        dtos[1].Role.Should().Be("assistant");
        var call = dtos[1].ToolCalls.Should().ContainSingle().Subject;
        call.CallId.Should().Be("call-1");
        call.ToolName.Should().Be("daedalus__search_learnings");
        call.ArgumentsJson.Should().Be("{\"query\":\"npgsql\"}");
        call.ResultPreview.Should().Be("[{\"pattern\":\"timeout\"}]");
        call.Succeeded.Should().BeNull("the transcript does not record tool outcomes");

        dtos[2].Text.Should().Be("Found one learning about timeouts.");
        dtos[2].ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public void ToDtos_leaves_result_preview_null_for_calls_without_a_result()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call-9", "roslyn__find_callers", null)]),
        };

        var call = AgentDtoMapper.ToDtos(messages).Should().ContainSingle().Subject.ToolCalls.Single();

        call.ArgumentsJson.Should().BeNull();
        call.ResultPreview.Should().BeNull();
    }

    [Fact]
    public void ToDto_maps_agent_definition_session_record_and_turn_result()
    {
        var agentId = AgentId.New();
        var definition = new AgentDefinition { Id = agentId, Name = "a", Description = "d", Instructions = "i", Tools = ["daedalus__*"] };
        var summary = AgentDtoMapper.ToDto(definition);
        summary.Should().BeEquivalentTo(new { Id = agentId.ToString(), Name = "a", Description = "d" });
        summary.Tools.Should().Equal("daedalus__*");

        var created = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);
        var record = new AgentSessionRecord(Session, agentId, "alice", SessionState.Running, created, created.AddMinutes(1), 3, 100, 50);
        AgentDtoMapper.ToDto(record).Should().BeEquivalentTo(new
        {
            Id = Session.ToString(),
            AgentId = agentId.ToString(),
            OwnerId = "alice",
            State = "Running",
            CreatedAt = created,
            LastActivityAt = created.AddMinutes(1),
            TurnCount = 3,
            TotalInputTokens = 100L,
            TotalOutputTokens = 50L,
        });

        var callId = ToolCallId.New();
        var result = new AgentTurnResult(Turn, Session, "done", new TurnUsage(10, 5, "claude-sonnet-5"),
            [new ToolCallSummary(callId, "daedalus__search_learnings", "{}", true, "ok", TimeSpan.FromMilliseconds(12))], TimeSpan.FromMilliseconds(1500));
        var dto = AgentDtoMapper.ToDto(result);
        dto.TurnId.Should().Be(Turn.ToString());
        dto.Text.Should().Be("done");
        dto.Usage.Should().Be(new Daedalus.Application.DTOs.Agents.TurnUsageDto(10, 5, "claude-sonnet-5"));
        dto.ElapsedMs.Should().Be(1500);
        dto.ToolCalls.Should().ContainSingle().Which.Should().BeEquivalentTo(new { CallId = callId.ToString(), ToolName = "daedalus__search_learnings", ArgumentsJson = "{}", ResultPreview = "ok", Succeeded = (bool?)true });
    }

    [Fact]
    public void ToDto_maps_default_usage_of_a_failed_turn_to_an_empty_model_id()
    {
        var failed = AgentDtoMapper.ToDto(new TurnFailedEvent(Session, Turn, AgentError.Cancelled()));

        failed.Usage.Should().Be(new Daedalus.Application.DTOs.Agents.TurnUsageDto(0, 0, ""));
    }

    [Fact]
    public void ToDto_maps_each_event_kind()
    {
        var callId = ToolCallId.New();
        var usage = new TurnUsage(7, 3, "m");
        var result = new AgentTurnResult(Turn, Session, "text", usage, [], TimeSpan.Zero);
        var error = AgentError.Quarantined("Blocked by Sentinel.", "Critical: PromptInjectionDetector");

        var text = AgentDtoMapper.ToDto(new TextDeltaEvent(Session, Turn, "hel"));
        text.Kind.Should().Be(AgentEventKinds.TextDelta);
        text.Text.Should().Be("hel");

        var started = AgentDtoMapper.ToDto(new ToolCallStartedEvent(Session, Turn, callId, "daedalus__search_learnings", "{\"query\":\"x\"}"));
        started.Kind.Should().Be(AgentEventKinds.ToolCall);
        started.ToolCall.Should().BeEquivalentTo(new { CallId = callId.ToString(), ToolName = "daedalus__search_learnings", ArgumentsJson = "{\"query\":\"x\"}", ResultPreview = (string?)null, Succeeded = (bool?)null });

        var finished = AgentDtoMapper.ToDto(new ToolCallFinishedEvent(Session, Turn, callId, "daedalus__search_learnings", false, "preview", TimeSpan.FromMilliseconds(3)));
        finished.Kind.Should().Be(AgentEventKinds.ToolResult);
        finished.ToolCall.Should().BeEquivalentTo(new { CallId = callId.ToString(), ToolName = "daedalus__search_learnings", ArgumentsJson = (string?)null, ResultPreview = "preview", Succeeded = (bool?)false });

        var usageDto = AgentDtoMapper.ToDto(new UsageEvent(Session, Turn, usage));
        usageDto.Kind.Should().Be(AgentEventKinds.Usage);
        usageDto.Usage.Should().BeEquivalentTo(new { InputTokens = 7, OutputTokens = 3, ModelId = "m" });

        var done = AgentDtoMapper.ToDto(new TurnCompletedEvent(Session, Turn, result));
        done.Kind.Should().Be(AgentEventKinds.Done);
        done.Result!.TurnId.Should().Be(Turn.ToString());

        var failed = AgentDtoMapper.ToDto(new TurnFailedEvent(Session, Turn, error, usage));
        failed.Kind.Should().Be(AgentEventKinds.Error);
        failed.ErrorCode.Should().Be("Quarantined");
        failed.ErrorMessage.Should().Be("Blocked by Sentinel.");
        failed.ErrorDetail.Should().Be("Critical: PromptInjectionDetector");
        failed.Usage.Should().BeEquivalentTo(new { InputTokens = 7, OutputTokens = 3, ModelId = "m" });
    }
}
