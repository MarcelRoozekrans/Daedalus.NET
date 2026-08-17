namespace Daedalus.Web.Services;

/// <summary>DTO for resuming an abandoned task.</summary>
public record ResumeTaskDto(Guid? NewSessionId);
