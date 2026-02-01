using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Parses LLM responses inline to extract structured, actionable learnings
///     without requiring file I/O or additional LLM calls.
///     Replaces the subagent-based extraction approach with zero-cost pattern matching.
/// </summary>
public interface ILlmResponseParser
{
    /// <summary>
    ///     Parses an LLM response from a single iteration and extracts structured learnings.
    ///     Designed to run inline after every iteration — no file writes, no subagent calls.
    /// </summary>
    /// <param name="llmResponse">The raw LLM response text.</param>
    /// <param name="iteration">The current iteration number.</param>
    /// <param name="completionPromise">The target completion promise string.</param>
    /// <param name="previousLearnings">Previously accumulated learnings for deduplication.</param>
    /// <returns>Parsed learnings from this iteration.</returns>
    Result<ParsedIterationLearnings> ParseResponse(
        string llmResponse,
        int iteration,
        string completionPromise,
        string? previousLearnings);
}
