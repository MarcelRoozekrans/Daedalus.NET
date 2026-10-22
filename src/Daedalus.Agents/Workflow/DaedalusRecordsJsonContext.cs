using System.Text.Json;
using System.Text.Json.Serialization;
using Daedalus.Agents.Decisions;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Generated metadata for the run-record payloads Daedalus.Agents writes, and for the decision state it sends Minos,
///     with the serializer's default options: the default encoder, names as declared, nulls written. These are the
///     options the reflection-based calls used, so the stored and wire bytes stay the same; <c>RunRecordGoldenTests</c>
///     pins them.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(RunRecordJson.SandboxRestore))]
[JsonSerializable(typeof(RunRecordJson.TestResult))]
[JsonSerializable(typeof(RunRecordJson.LensEvidence))]
[JsonSerializable(typeof(RunRecordJson.WorkspaceWrite))]
[JsonSerializable(typeof(FindingRecords.Dropped))]
[JsonSerializable(typeof(FindingRecords.DropVoided))]
[JsonSerializable(typeof(FindingRecords.Filed))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(NodeSelectionRecord.Payload))]
[JsonSerializable(typeof(DecisionStateJson.DecisionState))]
internal sealed partial class DaedalusRecordsJsonContext : JsonSerializerContext;
