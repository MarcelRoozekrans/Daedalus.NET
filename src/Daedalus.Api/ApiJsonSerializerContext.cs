using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using ExecutionSessionDto = Daedalus.Application.DTOs.ExecutionSessionDto;
using TaskDto = Daedalus.Application.DTOs.TaskDto;
using TaskExecutionDto = Daedalus.Application.DTOs.TaskExecutionDto;

namespace Daedalus.Api;

/// <summary>
///     JSON serialization context for AOT compatibility.
///     Enables compile-time serialization metadata generation for types used in API responses.
/// </summary>
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
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
internal partial class ApiJsonSerializerContext : JsonSerializerContext
{
}
