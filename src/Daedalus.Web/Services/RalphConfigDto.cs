namespace Daedalus.Web.Services;

/// <summary>DTO for Ralph Loop configuration.</summary>
public record RalphConfigDto(
    int IterationDelayMs,
    int MaxConsecutiveFailures,
    int MaxIterations,
    int RequestTimeoutSeconds,
    bool EnableDetailedLogging,
    RalphPromptConfigDto PromptOptions);
