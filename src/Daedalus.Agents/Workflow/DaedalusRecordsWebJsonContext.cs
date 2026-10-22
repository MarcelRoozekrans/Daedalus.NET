using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Generated metadata for the model-written review evidence, read leniently with the Web defaults the
///     reflection-based reads used (Review Focus 1): keys in any casing, and numbers sent as strings, so a finding's
///     <c>"line":"12"</c> still reads as 12. <c>LenientReadGoldenTests</c> pins both.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ReviewFinding[]))]
[JsonSerializable(typeof(DeferredFinding[]))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class DaedalusRecordsWebJsonContext : JsonSerializerContext;
