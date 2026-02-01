namespace Daedalus.Application.DTOs;

/// <summary>DTO for Ralph Loop configuration (runtime-editable).</summary>
public record RalphConfigDto(
    int IterationDelayMs,
    int MaxConsecutiveFailures,
    int MaxIterations,
    int RequestTimeoutSeconds,
    bool EnableDetailedLogging,
    RalphPromptConfigDto PromptOptions);
