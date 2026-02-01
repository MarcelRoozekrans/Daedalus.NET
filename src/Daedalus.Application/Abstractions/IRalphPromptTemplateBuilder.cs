using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Builds structured Ralph loop prompts from composable sections,
///     following the RLP numbered priority convention.
///     Implements the core prompting patterns described in the Ralph Wiggum technique.
/// </summary>
public interface IRalphPromptTemplateBuilder
{
    /// <summary>
    ///     Assembles a complete Ralph loop prompt from the configured sections.
    ///     Sections are ordered by priority (lower = first) following the RLP pattern.
    /// </summary>
    /// <param name="options">Configuration for prompt assembly.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assembled prompt string or failure.</returns>
    Task<Result<string>> BuildPromptAsync(RalphPromptTemplateOptions options, CancellationToken ct = default);

    /// <summary>
    ///     Gets the default RLP sections for a given configuration.
    ///     Useful for inspection and customization before assembly.
    /// </summary>
    /// <param name="options">Configuration for section generation.</param>
    /// <returns>Ordered list of prompt sections.</returns>
    Result<IReadOnlyList<PromptSection>> GetDefaultSections(RalphPromptTemplateOptions options);
}
