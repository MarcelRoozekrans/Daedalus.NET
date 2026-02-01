using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Injects up-to-date, version-specific documentation from Context7 into prompts.
///     Helps ground LLM responses in current APIs and prevents hallucinated solutions.
///     Dynamically resolves libraries via Context7 MCP to support comprehensive library detection.
/// </summary>
public interface IContext7DocumentationInjector
{
    /// <summary>
    ///     Analyzes prompt content to detect relevant libraries and fetches their documentation.
    /// </summary>
    /// <param name="prompt">The prompt text to analyze for library references.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Formatted documentation context ready for injection into prompts.</returns>
    Task<Result<string>> GetDocumentationContextAsync(string prompt, CancellationToken ct);
}
