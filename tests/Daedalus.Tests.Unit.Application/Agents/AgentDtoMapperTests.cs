using Daedalus.Agents.Api;
using Microsoft.Extensions.AI;
using Thalos;
using Thalos.Memory;

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

    [Fact]
    public void ToDto_maps_memory_events()
    {
        var id = MemoryId.New();

        var recalled = AgentDtoMapper.ToDto(new MemoryRecalledEvent(Session, Turn, [id, MemoryId.New()], 180));
        recalled.Kind.Should().Be(AgentEventKinds.MemoryRecalled);
        recalled.Memory.Should().BeEquivalentTo(new { Count = 2, Chars = 180 });
        recalled.Memory!.Ids.Should().HaveCount(2).And.Contain(id.ToString());

        var stored = AgentDtoMapper.ToDto(new MemoryStoredEvent(Session, Turn, id, "fact", true));
        stored.Kind.Should().Be(AgentEventKinds.MemoryStored);
        stored.Memory.Should().BeEquivalentTo(new { MemoryId = id.ToString(), Kind = "fact", Deduped = true });

        var failed = AgentDtoMapper.ToDto(new MemoryRecallFailedEvent(Session, Turn, AgentErrorCode.MemoryIndexUnavailable));
        failed.Kind.Should().Be(AgentEventKinds.MemoryRecallFailed);
        failed.Memory!.Code.Should().Be("MemoryIndexUnavailable");

        var pending = AgentDtoMapper.ToDto(new MemoryIndexPendingEvent(Session, Turn, id));
        pending.Kind.Should().Be(AgentEventKinds.MemoryIndexPending);
        pending.Memory!.MemoryId.Should().Be(id.ToString());

        var quarantined = AgentDtoMapper.ToDto(new MemoryQuarantinedEvent(Session, Turn, id, "High: SEC-01"));
        quarantined.Kind.Should().Be(AgentEventKinds.MemoryQuarantined);
        quarantined.Memory.Should().BeEquivalentTo(new { MemoryId = id.ToString(), Detail = "High: SEC-01" });
    }

    [Fact]
    public void ToDto_maps_memory_record_and_flags_shared_owner()
    {
        var id = MemoryId.New();
        var agentId = AgentId.New();
        var created = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);
        var record = new MemoryRecord
        {
            Id = id,
            OwnerId = "daedalus",
            AgentId = agentId,
            Kind = MemoryKind.Learning,
            Text = "Npgsql times out on long migrations.",
            Tags = ["database", "timeout"],
            Source = "ralph:task/1",
            Importance = 0.7,
            CreatedAt = created,
            UpdatedAt = created.AddMinutes(5),
            LastRecalledAt = created.AddHours(1),
            RecallCount = 3,
            IsArchived = false,
            IndexPending = true,
        };

        var dto = AgentDtoMapper.ToDto(record, "daedalus");

        dto.Should().BeEquivalentTo(new
        {
            Id = id.ToString(),
            OwnerId = "daedalus",
            AgentId = agentId.ToString(),
            Kind = "learning",
            Text = "Npgsql times out on long migrations.",
            Source = "ralph:task/1",
            Importance = 0.7,
            CreatedAt = created,
            UpdatedAt = created.AddMinutes(5),
            LastRecalledAt = (DateTimeOffset?)created.AddHours(1),
            RecallCount = 3,
            IsArchived = false,
            IndexPending = true,
            IsShared = true,
        });
        dto.Tags.Should().Equal("database", "timeout");
    }

    [Theory]
    [InlineData("alice", "daedalus", false)]
    [InlineData("daedalus", "daedalus", true)]
    [InlineData("Daedalus", "daedalus", false)]
    [InlineData("daedalus", null, false)]
    public void ToDto_marks_only_the_shared_owner_as_shared(string ownerId, string? sharedOwnerId, bool expected)
    {
        var record = new MemoryRecord
        {
            Id = MemoryId.New(),
            OwnerId = ownerId,
            Kind = MemoryKind.Note,
            Text = "t",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        var dto = AgentDtoMapper.ToDto(record, sharedOwnerId);

        dto.IsShared.Should().Be(expected);
        dto.AgentId.Should().BeNull();
    }

    [Fact]
    public void ToDto_passes_unknown_event_kinds_through_instead_of_killing_the_stream()
    {
        var dto = AgentDtoMapper.ToDto(new UnknownEvent(Session, Turn));

        dto.Kind.Should().Be("unknown-test-event");
        dto.Memory.Should().BeNull();
    }

    private sealed record UnknownEvent(SessionId SessionId, TurnId TurnId) : AgentEvent(SessionId, TurnId)
    {
        public override string Kind => "unknown-test-event";
    }

    /// <summary>
    ///     Skills have no UI (design section 6), and the catalogue provider never fails a turn — it logs, raises this
    ///     event and proceeds without a catalogue. So the event is deliberately <b>not</b> given a DTO arm: it reaches
    ///     the client by kind through the forward-compatible default, which is also what protects the stream against
    ///     event types a newer Thalos adds. This fact is the decision; without it the pass-through is an accident.
    /// </summary>
    [Fact]
    public void Skill_catalogue_failed_reaches_the_client_as_kind_only()
    {
        var evt = new SkillCatalogueFailedEvent(SessionId.New(), TurnId.New(), AgentErrorCode.SkillStoreFailed);

        var dto = AgentDtoMapper.ToDto(evt);

        dto.Kind.Should().Be("skill-catalogue-failed");
        dto.Text.Should().BeNull();
        dto.Memory.Should().BeNull();
        dto.ErrorCode.Should().BeNull("a skills diagnostic is not a turn failure and must not render as one");
    }
}
