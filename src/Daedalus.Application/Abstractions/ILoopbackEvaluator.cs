using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Evaluates LLM output by executing commands and feeding results back into the loop.
///     The article's key insight: "Always look for opportunities to loop Ralph back on itself.
///     This could be as simple as instructing it to add additional logging."
///     The agentic loop pattern: execute tool → evaluate result → feed back into context.
/// </summary>
public interface ILoopbackEvaluator
{
    /// <summary>
    ///     Evaluates the LLM response by running build/test commands and capturing output.
    ///     This is the "tool execution → evaluation → context allocation" cycle.
    /// </summary>
    /// <param name="workspacePath">Root directory of the workspace.</param>
    /// <param name="llmResponse">The LLM response from the current iteration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Evaluation result with build/test output for feeding back into the next iteration.</returns>
    Task<Result<LoopbackResult>> EvaluateAsync(
        string workspacePath,
        string llmResponse,
        CancellationToken ct);

    /// <summary>
    ///     Runs a specific command and captures its output for loop-back injection.
    /// </summary>
    /// <param name="workspacePath">Working directory for the command.</param>
    /// <param name="command">The command to execute.</param>
    /// <param name="arguments">Command arguments.</param>
    /// <param name="timeoutSeconds">Timeout before killing the process.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Command execution result.</returns>
    Task<Result<CommandExecutionResult>> RunCommandAsync(
        string workspacePath,
        string command,
        string arguments,
        int timeoutSeconds = 120,
        CancellationToken ct = default);
}
