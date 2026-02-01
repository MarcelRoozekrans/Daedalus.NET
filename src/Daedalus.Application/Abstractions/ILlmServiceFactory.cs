using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Factory for resolving LLM service implementations by provider name.
///     Supports the dual-provider architecture where Copilot and Claude
///     can coexist and be selected based on task requirements.
/// </summary>
public interface ILlmServiceFactory
{
    /// <summary>
    ///     Gets the default LLM service based on configuration.
    /// </summary>
    ILlmService GetDefault();

    /// <summary>
    ///     Gets an LLM service by provider name (e.g., "copilot", "claude").
    /// </summary>
    /// <param name="providerName">The provider to resolve.</param>
    /// <returns>The LLM service or failure if provider not registered.</returns>
    Result<ILlmService> GetByProvider(string providerName);

    /// <summary>
    ///     Gets an LLM service that supports subagents.
    ///     Returns the first registered provider with subagent capability,
    ///     or failure if none available.
    /// </summary>
    Result<ILlmService> GetWithSubagentSupport();

    /// <summary>
    ///     Gets all registered provider names.
    /// </summary>
    IReadOnlyList<string> GetAvailableProviders();
}
