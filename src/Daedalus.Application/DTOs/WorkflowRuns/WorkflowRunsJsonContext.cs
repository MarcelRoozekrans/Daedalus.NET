using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daedalus.Application.DTOs.WorkflowRuns;

/// <summary>Source-generated JSON for the run hub's messages, shared by the API and the Web so both trim safely.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WorkflowRunChanged))]
public sealed partial class WorkflowRunsJsonContext : JsonSerializerContext;
