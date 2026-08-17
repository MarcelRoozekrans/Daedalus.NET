namespace Daedalus.Application.DTOs;

/// <summary>DTO for ExecutionSession entity.</summary>
public record ExecutionSessionDto(
    Guid Id,
    string WorkerName,
    DateTime StartedAt,
    DateTime LastHeartbeat,
    bool IsActive,
    int TasksCompleted);
