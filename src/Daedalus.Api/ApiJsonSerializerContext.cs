using System.Text.Json;
using System.Text.Json.Serialization;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Application.DTOs.WorkflowRuns;
using Microsoft.AspNetCore.Mvc;
using TaskDto = Daedalus.Application.DTOs.TaskDto;
using TaskExecutionDto = Daedalus.Application.DTOs.TaskExecutionDto;

namespace Daedalus.Api;

/// <summary>
///     JSON type metadata for the minimal API endpoints, and the serializer for the exception handler's problem body.
/// </summary>
/// <remarks>
///     The endpoints take this context as the only entry in their <c>TypeInfoResolverChain</c>
///     (<see cref="Endpoints.DaedalusEndpoints.AddDaedalusEndpoints"/>), with no reflection fallback, so a request or
///     response type that is not listed here fails at the first call rather than serializing by reflection. In that
///     chain the context only decides which types resolve: a context placed in another options instance's chain takes
///     that instance's runtime settings, so the endpoints' nulls, naming, case and number handling come from
///     <c>Microsoft.AspNetCore.Http.Json.JsonOptions</c> (web defaults, <c>DefaultIgnoreCondition</c> at <c>Never</c>,
///     as MVC's). The options declared below apply only through <c>ApiJsonSerializerContext.Default</c>, which today
///     means <c>GlobalExceptionHandler</c>. <c>ProblemDetails</c>
///     carries its own <c>WhenWritingNull</c> on its members, so its body is the same under <c>Never</c>. SSE events
///     do not use this context: see <see cref="AgentEventStreamJsonContext"/>.
/// </remarks>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(TaskDto))]
[JsonSerializable(typeof(TaskExecutionDto))]
[JsonSerializable(typeof(ProjectDto))]
[JsonSerializable(typeof(PagedResultDto<TaskDto>))]
[JsonSerializable(typeof(PagedResultDto<TaskExecutionDto>))]
[JsonSerializable(typeof(PagedResultDto<ProjectDto>))]
[JsonSerializable(typeof(PrdResponseDto))]
[JsonSerializable(typeof(List<TaskDto>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
// The endpoints' { "error" } body, which replaces the controllers' anonymous objects (Task 5)
[JsonSerializable(typeof(Endpoints.ErrorBody))]
[JsonSerializable(typeof(Endpoints.ErrorDetailBody))]
// Thalos agents (AgentEndpoints / AgentSessionsEndpoints)
[JsonSerializable(typeof(AgentSummaryDto))]
[JsonSerializable(typeof(List<AgentSummaryDto>))]
[JsonSerializable(typeof(AgentSessionDto))]
[JsonSerializable(typeof(List<AgentSessionDto>))]
[JsonSerializable(typeof(AgentSessionDetailDto))]
[JsonSerializable(typeof(AgentMessageDto))]
[JsonSerializable(typeof(AgentToolCallDto))]
[JsonSerializable(typeof(TurnUsageDto))]
[JsonSerializable(typeof(AgentTurnResultDto))]
[JsonSerializable(typeof(MemoryEventDto))]
[JsonSerializable(typeof(MemoryDto))]
[JsonSerializable(typeof(PagedResultDto<MemoryDto>))]
[JsonSerializable(typeof(CreateAgentSessionResponseDto))]
[JsonSerializable(typeof(SendTurnRequestDto))]
// The endpoints' response types, once the controller actions', so OpenAPI generation and the endpoints resolve
// them (Task 4, R26)
[JsonSerializable(typeof(AnalysisStatusDto))]
[JsonSerializable(typeof(BrainstormMessageDto))]
[JsonSerializable(typeof(BrainstormSessionDto))]
[JsonSerializable(typeof(BrainstormSessionSummaryDto))]
[JsonSerializable(typeof(CodeAnalysisRequestDto))]
[JsonSerializable(typeof(CostEstimateDto))]
[JsonSerializable(typeof(CostSummaryDto))]
[JsonSerializable(typeof(FinalizeAnalysisResponse))]
[JsonSerializable(typeof(ModelPricingDto))]
[JsonSerializable(typeof(ProjectCostDto))]
[JsonSerializable(typeof(RunDiagnosis))]
[JsonSerializable(typeof(TaskCostDto))]
[JsonSerializable(typeof(RepositoryConfigurationDto))]
[JsonSerializable(typeof(StartWorkflowRunResponse))]
[JsonSerializable(typeof(SubmitAnalysisResponse))]
[JsonSerializable(typeof(Endpoints.TestConnectionResponse))]
[JsonSerializable(typeof(Endpoints.CancelAnalysisResponse))]
[JsonSerializable(typeof(Endpoints.IterateAnalysisResponse))]
[JsonSerializable(typeof(WorkflowRunList))]
[JsonSerializable(typeof(WorkflowRunView))]
[JsonSerializable(typeof(IEnumerable<RepositoryConfigurationDto>))]
[JsonSerializable(typeof(IReadOnlyList<AgentSummaryDto>))]
[JsonSerializable(typeof(IReadOnlyList<AgentSessionDto>))]
[JsonSerializable(typeof(IReadOnlyList<BrainstormSessionSummaryDto>))]
[JsonSerializable(typeof(IReadOnlyList<CodeAnalysisRequestDto>))]
[JsonSerializable(typeof(IReadOnlyList<ModelPricingDto>))]
[JsonSerializable(typeof(IReadOnlyList<ProjectCostDto>))]
[JsonSerializable(typeof(IReadOnlyList<RunDiagnosis>))]
[JsonSerializable(typeof(IReadOnlyList<TaskCostDto>))]
// Request bodies
[JsonSerializable(typeof(CancelWorkflowRunRequest))]
[JsonSerializable(typeof(CreateBrainstormSessionDto))]
[JsonSerializable(typeof(CreateProjectDto))]
[JsonSerializable(typeof(CreateTaskDto))]
[JsonSerializable(typeof(FinalizeAnalysisRequest))]
[JsonSerializable(typeof(IterateAnalysisRequest))]
[JsonSerializable(typeof(PrdConversionRequestDto))]
[JsonSerializable(typeof(PrdRequestDto))]
[JsonSerializable(typeof(ResumeWorkflowRunRequest))]
[JsonSerializable(typeof(SendBrainstormMessageDto))]
[JsonSerializable(typeof(Endpoints.StartWorkflowRunRequest))]
[JsonSerializable(typeof(SubmitAnalysisRequest))]
[JsonSerializable(typeof(UpdateProjectDto))]
[JsonSerializable(typeof(UpdateTaskDto))]
[JsonSerializable(typeof(CreateRepositoryConfigurationDto))]
[JsonSerializable(typeof(UpdateRepositoryConfigurationDto))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(Guid))]
internal partial class ApiJsonSerializerContext : JsonSerializerContext
{
}
