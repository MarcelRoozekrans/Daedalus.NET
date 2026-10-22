using System.Text.Json.Serialization;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Services.Brainstorm;

/// <summary>
///     Source-generated JSON for the task list a model returns from a brainstorm's plan. It reads as the reflective options
///     did: keys in any case, and an enum as its name in any case or as its number. <c>UseStringEnumConverter</c> adds
///     <c>JsonStringEnumConverter&lt;T&gt;</c> for each enum, whose default still accepts integers.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<PrdItemForConversionDto>))]
internal sealed partial class BrainstormJsonContext : JsonSerializerContext;
