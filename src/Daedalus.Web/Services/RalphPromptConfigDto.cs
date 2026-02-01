namespace Daedalus.Web.Services;

/// <summary>DTO for Ralph Prompt template configuration.</summary>
public record RalphPromptConfigDto(
    int MaxParallelSubagents,
    bool IncludeGitWorkflow,
    bool IncludeTestInstructions,
    bool IncludeQualityGuards,
    bool IncludeSelfImprovement,
    bool IncludeCodingStandards,
    bool IncludeLoggingInstructions);
