using System.Text.Json;
using System.Text.Json.Serialization;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Application.DTOs.WorkflowRuns;

namespace Daedalus.Web.Services;

/// <summary>
///     The body the writes that send none post: <c>{}</c>, as the anonymous <c>new { }</c> they replace did. It has no
///     property on purpose, so a property added here changes every one of those requests.
/// </summary>
internal sealed record EmptyBody
{
    /// <summary>The one value; it carries no state.</summary>
    public static EmptyBody Instance { get; } = new();
}

/// <summary>
///     A refused write's body in the two shapes the server answers: a ProblemDetails <c>detail</c>, and the
///     <c>{ "error": "..." }</c> most controllers return.
/// </summary>
internal sealed record FailureBody(string? Detail, string? Error);

/// <summary>
///     Source-generated JSON for the four Web API clients, so the Web reads and writes without reflection. It keeps the web
///     defaults that <c>System.Net.Http.Json</c> used: camelCase, case-insensitive reads and numbers in strings.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
// ApiClient: reads
[JsonSerializable(typeof(PagedResultDto<TaskDto>))]
[JsonSerializable(typeof(TaskDto))]
[JsonSerializable(typeof(PagedResultDto<ProjectDto>))]
[JsonSerializable(typeof(ProjectDto))]
[JsonSerializable(typeof(List<RunDiagnosis>))]
[JsonSerializable(typeof(CostSummaryDto))]
[JsonSerializable(typeof(List<ProjectCostDto>))]
[JsonSerializable(typeof(List<TaskCostDto>))]
[JsonSerializable(typeof(CostEstimateDto))]
[JsonSerializable(typeof(List<ModelPricingDto>))]
[JsonSerializable(typeof(BrainstormSessionDto))]
[JsonSerializable(typeof(List<BrainstormSessionSummaryDto>))]
[JsonSerializable(typeof(BrainstormMessageDto))]
[JsonSerializable(typeof(List<TaskDto>))]
[JsonSerializable(typeof(PrdResponseDto))]
[JsonSerializable(typeof(StartWorkflowRunResponse))]
[JsonSerializable(typeof(WorkflowRunList))]
[JsonSerializable(typeof(WorkflowRunView))]
[JsonSerializable(typeof(FailureBody))]
// ApiClient: writes
[JsonSerializable(typeof(CreateTaskDto))]
[JsonSerializable(typeof(UpdateTaskDto))]
[JsonSerializable(typeof(CreateBrainstormSessionDto))]
[JsonSerializable(typeof(SendBrainstormMessageDto))]
[JsonSerializable(typeof(PrdRequestDto))]
[JsonSerializable(typeof(PrdConversionRequestDto))]
[JsonSerializable(typeof(ResumeWorkflowRunRequest))]
[JsonSerializable(typeof(CancelWorkflowRunRequest))]
[JsonSerializable(typeof(EmptyBody))]
// ProjectApiClient
[JsonSerializable(typeof(CreateProjectDto))]
[JsonSerializable(typeof(UpdateProjectDto))]
// RepositoriesApiClient
[JsonSerializable(typeof(List<RepositoryConfigurationDto>))]
[JsonSerializable(typeof(CreateRepositoryConfigurationDto))]
[JsonSerializable(typeof(UpdateRepositoryConfigurationDto))]
// AgentApiClient
[JsonSerializable(typeof(IReadOnlyList<AgentSummaryDto>))]
[JsonSerializable(typeof(IReadOnlyList<AgentSessionDto>))]
[JsonSerializable(typeof(AgentSessionDetailDto))]
[JsonSerializable(typeof(CreateAgentSessionResponseDto))]
[JsonSerializable(typeof(PagedResultDto<MemoryDto>))]
[JsonSerializable(typeof(MemoryDto))]
[JsonSerializable(typeof(AgentEventDto))]
[JsonSerializable(typeof(SendTurnRequestDto))]
internal sealed partial class WebJsonContext : JsonSerializerContext;
