using System.Text.Json.Serialization;

namespace Daedalus.Agents.Tools;

/// <summary>
///     Generated metadata for the parameters and results of Daedalus' seven Thalos tool classes, so their typed
///     registrations bind and answer without reflection-based JSON. Every result is a <see cref="string"/>; the parameters
///     are strings, integers and one <see cref="Guid"/>. A <see cref="CancellationToken"/> and the bound
///     <c>ISecurityContext</c> are not serialized. The registrations take it through
///     <see cref="Thalos.Tools.LocalToolSources.CreateOptions"/>, never through its own <c>Options</c>; see
///     <c>DaedalusAgentsServiceCollectionExtensions.DaedalusToolOptions</c>.
/// </summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(Guid))]
internal sealed partial class DaedalusToolsJsonContext : JsonSerializerContext;
