using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Mappers;

/// <summary>
///     Maps brainstorm domain entities to their DTO representations.
/// </summary>
public static class BrainstormDtoMapper
{
    public static BrainstormSessionDto ToDto(BrainstormSession session) => new(
        session.Id,
        session.ProjectId,
        session.Phase,
        session.Messages.Select(ToDto).ToList(),
        session.DesignDocument,
        session.ImplementationPlan,
        session.PhaseCompleteSignaled,
        session.CreatedAt,
        session.CompletedAt);

    public static BrainstormMessageDto ToDto(BrainstormMessage message) => new(
        message.Id,
        message.Role.ToString(),
        message.Content,
        message.Phase,
        message.CreatedAt);

    public static BrainstormSessionSummaryDto ToSummaryDto(BrainstormSession session) => new(
        session.Id,
        session.ProjectId,
        session.Phase,
        session.Messages.Count,
        session.CreatedAt,
        session.CompletedAt);
}
