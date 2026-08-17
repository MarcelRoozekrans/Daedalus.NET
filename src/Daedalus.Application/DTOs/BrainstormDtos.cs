using Daedalus.Domain.Entities;

namespace Daedalus.Application.DTOs;

public record CreateBrainstormSessionDto(Guid ProjectId);

public record SendBrainstormMessageDto(string Content);

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
