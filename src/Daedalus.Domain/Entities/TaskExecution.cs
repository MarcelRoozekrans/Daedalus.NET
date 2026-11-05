namespace Daedalus.Domain.Entities;

/// <summary>
///     Records a single iteration of the retired task loop (phase 2.8), kept as task history.
/// </summary>
public sealed class TaskExecution : Entity<Guid>
{
    /// <summary>Gets the task this execution belongs to.</summary>
    public Guid TaskId { get; init; }

    /// <summary>Gets the session that performed this execution.</summary>
    public Guid SessionId { get; init; }

    /// <summary>Gets which iteration this is (1-based).</summary>
    public int IterationNumber { get; init; }

    /// <summary>Gets the prompt sent to the LLM.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>Gets the complete LLM response.</summary>
    public string LlmResponse { get; init; } = string.Empty;

    /// <summary>Gets whether the completion promise was found in the response.</summary>
    public bool CompletionPromiseFound { get; init; }

    /// <summary>Gets when this execution occurred.</summary>
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Gets how long the LLM invocation took.</summary>
    public TimeSpan ExecutionDuration { get; init; }

    /// <summary>Gets any error that occurred during execution.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the number of input tokens consumed by this invocation.</summary>
    public int InputTokens { get; init; }

    /// <summary>Gets the number of output tokens produced by this invocation.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Gets the model ID used for this invocation (e.g., "claude-sonnet-5").</summary>
    public string? ModelId { get; init; }

    /// <summary>Rebuilds a stored execution record, for persistence only: it sets every value as given and validates nothing.</summary>
    /// <param name="id">The record's id.</param>
    /// <param name="taskId">The task it belongs to.</param>
    /// <param name="sessionId">The session that performed it.</param>
    /// <param name="iterationNumber">The iteration number.</param>
    /// <param name="prompt">The prompt sent.</param>
    /// <param name="llmResponse">The response received.</param>
    /// <param name="completionPromiseFound">Whether the completion promise was found.</param>
    /// <param name="executedAt">When it ran.</param>
    /// <param name="executionDuration">How long the invocation took.</param>
    /// <param name="error">The error, if any.</param>
    /// <param name="inputTokens">The input tokens consumed.</param>
    /// <param name="modelId">The model used, if recorded.</param>
    /// <param name="outputTokens">The output tokens produced.</param>
    /// <returns>The execution record.</returns>
    public static TaskExecution Rehydrate(
        Guid id,
        Guid taskId,
        Guid sessionId,
        int iterationNumber,
        string prompt,
        string llmResponse,
        bool completionPromiseFound,
        DateTime executedAt,
        TimeSpan executionDuration,
        string? error,
        int inputTokens,
        string? modelId,
        int outputTokens) => new()
        {
            Id = id,
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = iterationNumber,
            Prompt = prompt,
            LlmResponse = llmResponse,
            CompletionPromiseFound = completionPromiseFound,
            ExecutedAt = executedAt,
            ExecutionDuration = executionDuration,
            Error = error,
            InputTokens = inputTokens,
            ModelId = modelId,
            OutputTokens = outputTokens,
        };
}
