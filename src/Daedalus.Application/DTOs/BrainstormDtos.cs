using Daedalus.Domain.Entities;
using ZeroAlloc.Validation;

namespace Daedalus.Application.DTOs;

public record CreateBrainstormSessionDto(Guid ProjectId);

[Validate]
public record SendBrainstormMessageDto(
    [property: NotEmpty(Message = "Message content is required.")]
    [property: MaxLength(10000, Message = "Message cannot exceed 10,000 characters.")]
    string Content);

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
