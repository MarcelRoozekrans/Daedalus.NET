namespace Daedalus.Web.Services;

public record TaskExecutionDto(
    Guid Id,
    Guid TaskId,
    Guid SessionId,
    int IterationNumber,
    string Prompt,
    string LlmResponse,
    bool CompletionPromiseFound,
    DateTime ExecutedAt,
    TimeSpan ExecutionDuration,
    string? Error);
