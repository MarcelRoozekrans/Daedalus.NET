using Daedalus.Application.Configuration;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Context passed through each middleware stage during a single Ralph loop iteration.
///     Accumulates data as it flows through the pipeline.
/// </summary>
public sealed class RalphIterationContext
{
    /// <summary>
    ///     The task being executed.
    /// </summary>
    public required Task Task { get; init; }

    /// <summary>
    ///     The session ID for this execution.
    /// </summary>
    public required Guid SessionId { get; init; }

    /// <summary>
    ///     The prompt context (includes history, learnings, etc).
    /// </summary>
    public required PromptContext PromptContext { get; set; }

    /// <summary>
    ///     Current iteration number (1-based).
    /// </summary>
    public required int Iteration { get; init; }

    /// <summary>
    ///     Ralph loop configuration.
    /// </summary>
    public required RalphLoopConfiguration Configuration { get; init; }

    /// <summary>
    ///     The prompt built for this iteration.
    ///     Populated by PromptBuildingMiddleware.
    /// </summary>
    public string? IterationPrompt { get; set; }

    /// <summary>
    ///     The LLM response for this iteration.
    ///     Populated by LlmInvocationMiddleware.
    /// </summary>
    public string? LlmResponse { get; set; }

    /// <summary>
    ///     Duration of the LLM invocation.
    ///     Populated by LlmInvocationMiddleware.
    /// </summary>
    public TimeSpan InvocationDuration { get; set; }

    /// <summary>
    ///     Whether the LLM invocation succeeded.
    ///     Populated by LlmInvocationMiddleware.
    /// </summary>
    public bool LlmInvocationSucceeded { get; set; }

    /// <summary>
    ///     Whether the completion promise was found in the LLM response.
    ///     Populated by CompletionDetectionMiddleware.
    /// </summary>
    public bool CompletionPromiseFound { get; set; }

    /// <summary>
    ///     Result of loopback evaluation (build/test verification).
    ///     Populated by LoopbackEvaluationMiddleware.
    /// </summary>
    public LoopbackResult? LoopbackResult { get; set; }

    /// <summary>
    ///     Whether the loop should terminate after this iteration.
    /// </summary>
    public bool ShouldTerminateLoop { get; set; }

    /// <summary>
    ///     Reason for loop termination if ShouldTerminateLoop is true.
    /// </summary>
    public string? TerminationReason { get; set; }

    /// <summary>
    ///     Count of consecutive failures across iterations.
    ///     Tracked and updated by failure handlers.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    ///     Structured learnings parsed inline from the LLM response.
    ///     Populated by InlineLearningsExtractionMiddleware (Order 350).
    /// </summary>
    public ParsedIterationLearnings? ParsedLearnings { get; set; }

    /// <summary>
    ///     Extensible metadata bag for middleware communication.
    /// </summary>
    public IDictionary<string, object> Items { get; } = new Dictionary<string, object>(StringComparer.Ordinal);
}
