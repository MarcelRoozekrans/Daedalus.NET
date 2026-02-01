using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

#pragma warning restore CA1002, MA0016

/// <summary>
///     Builds context-aware prompts for Ralph loop iterations,
///     incorporating execution history and session state.
///     Inspired by the Ralph Wiggum loop pattern.
/// </summary>
public interface IPromptBuilder
{
    /// <summary>
    ///     Initializes a new prompt context for a task.
    /// </summary>
    Task<Result<PromptContext>> InitializeContextAsync(
        Guid taskId,
        Guid sessionId,
        string originalPrompt,
        string completionPromise,
        string? learnings,
        CancellationToken ct);

    /// <summary>
    ///     Builds the actual prompt to send to the LLM for the current iteration,
    ///     incorporating history and execution context.
    /// </summary>
    /// <remarks>
    ///     This is where the "Ralph loop" intelligence lives:
    ///     - First iteration: Use original prompt as-is
    ///     - Subsequent iterations: Enhance with conversation history and hints about what was tried
    ///     - Can inject feedback about what the LLM should focus on next
    ///     - Can provide state about task progress
    /// </remarks>
    Task<Result<string>> BuildIterationPromptAsync(
        PromptContext context,
        CancellationToken ct);

    /// <summary>
    ///     Updates the context with the result of an LLM invocation,
    ///     tracking the history for subsequent iterations.
    /// </summary>
    Task<Result> RecordIterationResultAsync(
        PromptContext context,
        int iterationNumber,
        string prompt,
        string response,
        bool completionPromiseFound,
        TimeSpan duration,
        string? executionError,
        CancellationToken ct);

    /// <summary>
    ///     Persists the prompt context to storage for session state tracking.
    /// </summary>
    Task<Result> PersistContextAsync(
        PromptContext context,
        CancellationToken ct);

    /// <summary>
    ///     Retrieves a previously saved prompt context.
    /// </summary>
    Task<Result<PromptContext>> LoadContextAsync(
        Guid taskId,
        Guid sessionId,
        CancellationToken ct);
}
