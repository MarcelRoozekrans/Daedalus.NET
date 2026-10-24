using System.Text.Json;
using System.Text.Json.Serialization;
using Daedalus.Infrastructure.Agents.Tools;

namespace Daedalus.Infrastructure.Agents;

/// <summary>
///     Generated metadata for the in-process MCP tools: the result records they serialise, and the parameter and return
///     types <see cref="McpToolBuilder"/> hands to <c>AIFunctionFactory</c>. Its own options are the serializer's
///     defaults with <c>WriteIndented</c>, the options the tools serialised their anonymous results with before phase
///     3.2: the default encoder, names as declared, nulls written. As a tool's resolver it is combined with M.E.AI's, under
///     M.E.AI's options, so a tool result keeps M.E.AI's encoder. <c>McpToolOracleTests</c> pins both.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.General, WriteIndented = true)]
[JsonSerializable(typeof(DaedalusLearningsTools.LearningResult[]))]
[JsonSerializable(typeof(DaedalusFailurePatternsTools.FailurePatternResult[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
internal sealed partial class McpToolsJsonContext : JsonSerializerContext;
