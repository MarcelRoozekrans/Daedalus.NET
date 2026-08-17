namespace Daedalus.Application.DTOs;

/// <summary>DTO for resuming an abandoned task.</summary>
public record ResumeTaskDto(Guid? NewSessionId);
