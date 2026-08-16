namespace Daedalus.Web.Services;

public record ExecutionSessionDto(
    Guid Id,
    string WorkerName,
    DateTime StartedAt,
    DateTime LastHeartbeat,
    bool IsActive,
    int TasksCompleted);
