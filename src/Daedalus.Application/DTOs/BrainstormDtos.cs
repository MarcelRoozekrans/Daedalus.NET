using System.Diagnostics.CodeAnalysis;
using Daedalus.Domain.Entities;
using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

public record CreateBrainstormSessionDto(Guid ProjectId);

[Validate]
public record SendBrainstormMessageDto(
    [property: Must(nameof(SendBrainstormMessageDto.IsPresent), Message = "Message content is required.")]
    [property: MaxLength(10000, Message = "Message cannot exceed 10,000 characters.")]
    string Content)
{
    /// <summary>
    ///     Backs the <c>[Must]</c> rule above. FluentValidation's <c>NotEmpty()</c> rejects
    ///     whitespace-only strings; ZeroAlloc.Validation's <c>[NotEmpty]</c> lowers to
    ///     <c>string.IsNullOrEmpty</c>, which does not, so a plain <c>[NotEmpty]</c> here would
    ///     silently accept "   ".
    /// </summary>
    [SuppressMessage("Performance", "CA1822", Justification = "ZeroAlloc.Validation's [Must] attribute requires an instance method.")]
    [SuppressMessage("Major Code Smell", "S2325", Justification = "ZeroAlloc.Validation's [Must] attribute requires an instance method.")]
    internal bool IsPresent(string value) => !string.IsNullOrWhiteSpace(value);
}

public record BrainstormSessionDto(
    Guid Id,
    Guid ProjectId,
    BrainstormPhase Phase,
    IReadOnlyList<BrainstormMessageDto> Messages,
    string? DesignDocument,
    string? ImplementationPlan,
    bool PhaseCompleteSignaled,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public record BrainstormMessageDto(
    Guid Id,
    string Role,
    string Content,
    BrainstormPhase Phase,
    DateTime CreatedAt);

public record BrainstormSessionSummaryDto(
    Guid Id,
    Guid ProjectId,
    BrainstormPhase Phase,
    int MessageCount,
    DateTime CreatedAt,
    DateTime? CompletedAt);
