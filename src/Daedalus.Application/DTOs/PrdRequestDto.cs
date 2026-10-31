namespace Daedalus.Application.DTOs;

/// <summary>
///     Represents a user's requirements for PRD generation.
/// </summary>
/// <remarks>
///     Every member is declared, in the positional order, so the JSON member order stays the parameters' order.
///     <see cref="UserRequirements"/> and <see cref="MaxItems"/> have plain setters rather than <c>init</c> ones, so every
///     JSON contract leaves an omitted member at the parameterless constructor's value, <c>""</c> and 10.
///     Reflection-based <c>System.Text.Json</c>, which MVC uses, does that for <c>init</c> members too; the source
///     generator treats <c>init</c> members as constructor arguments and sets an omitted one to
///     <see langword="null"/>. Defaults on the primary constructor instead would make MVC treat
///     <see cref="UserRequirements"/> as optional and drop its implicit required check.
/// </remarks>
public record PrdRequestDto(
    Guid ProjectId,
    string UserRequirements,
    string? Context = null,
    int? MaxItems = 10)
{
    /// <summary>
    ///     Initializes a new instance of the PrdRequestDto record.
    /// </summary>
    public PrdRequestDto() : this(Guid.Empty, string.Empty)
    {
    }

    /// <summary>The project the PRD is for.</summary>
    public Guid ProjectId { get; init; } = ProjectId;

    /// <summary>The user's requirements; <c>""</c> when a JSON body omits them.</summary>
    public string UserRequirements { get; set; } = UserRequirements;

    /// <summary>Optional context for the generation.</summary>
    public string? Context { get; init; } = Context;

    /// <summary>The most PRD items to generate; 10 when a JSON body omits it.</summary>
    public int? MaxItems { get; set; } = MaxItems;
}
