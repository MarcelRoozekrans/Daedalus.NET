using System.Text.Json.Serialization;
using Daedalus.Application.DTOs.Agents;
using Microsoft.AspNetCore.Mvc;
using ExecutionSessionDto = Daedalus.Application.DTOs.ExecutionSessionDto;
using TaskDto = Daedalus.Application.DTOs.TaskDto;
using TaskExecutionDto = Daedalus.Application.DTOs.TaskExecutionDto;

namespace Daedalus.Api;

/// <summary>
///     JSON serialization context for AOT compatibility.
///     Enables compile-time serialization metadata generation for types used in API responses.
/// </summary>
/// <remarks>
///     This context is <em>not</em> plugged into MVC's <c>TypeInfoResolverChain</c> today: controller responses go through
///     the default reflection-based resolver, and only code that names the context explicitly uses it (the SSE writer
///     serialises with <c>ApiJsonSerializerContext.Default.AgentEventDto</c>). Entries here are therefore preparation for
///     trimming/AOT and a compile-time check that a response DTO is source-generatable — adding one changes no behaviour.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(TaskDto))]
[JsonSerializable(typeof(ExecutionSessionDto))]
[JsonSerializable(typeof(TaskExecutionDto))]
[JsonSerializable(typeof(ProjectDto))]
[JsonSerializable(typeof(PagedResultDto<TaskDto>))]
[JsonSerializable(typeof(PagedResultDto<ExecutionSessionDto>))]
[JsonSerializable(typeof(PagedResultDto<TaskExecutionDto>))]
[JsonSerializable(typeof(PagedResultDto<ProjectDto>))]
[JsonSerializable(typeof(PrdResponseDto))]
[JsonSerializable(typeof(List<TaskDto>))]
[JsonSerializable(typeof(ProblemDetails))]
// Thalos agents (AgentsController / AgentSessionsController)
[JsonSerializable(typeof(AgentSummaryDto))]
[JsonSerializable(typeof(List<AgentSummaryDto>))]
[JsonSerializable(typeof(AgentSessionDto))]
[JsonSerializable(typeof(List<AgentSessionDto>))]
[JsonSerializable(typeof(AgentSessionDetailDto))]
[JsonSerializable(typeof(AgentMessageDto))]
[JsonSerializable(typeof(AgentToolCallDto))]
[JsonSerializable(typeof(TurnUsageDto))]
[JsonSerializable(typeof(AgentTurnResultDto))]
[JsonSerializable(typeof(AgentEventDto))]
[JsonSerializable(typeof(MemoryEventDto))]
[JsonSerializable(typeof(MemoryDto))]
[JsonSerializable(typeof(PagedResultDto<MemoryDto>))]
[JsonSerializable(typeof(CreateAgentSessionResponseDto))]
[JsonSerializable(typeof(SendTurnRequestDto))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
internal partial class ApiJsonSerializerContext : JsonSerializerContext
{
}
