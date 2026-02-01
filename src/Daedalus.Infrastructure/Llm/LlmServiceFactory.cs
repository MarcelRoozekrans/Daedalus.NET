using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Llm;

/// <summary>
///     Resolves LLM service implementations by provider name.
///     Supports multiple providers (Copilot, Claude) coexisting in the same application.
/// </summary>
public sealed partial class LlmServiceFactory(
    IEnumerable<ILlmService> services,
    string defaultProvider,
    ILogger<LlmServiceFactory> logger) : ILlmServiceFactory
{
    private readonly Dictionary<string, ILlmService> _providers = services
        .ToDictionary(s => s.ProviderName, s => s, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ILlmService GetDefault()
    {
        if (_providers.TryGetValue(defaultProvider, out var service))
        {
            return service;
        }

        // Fall back to first available provider
        if (_providers.Count > 0)
        {
            var fallback = _providers.Values.First();
            LogFallingBackToProvider(logger, defaultProvider, fallback.ProviderName);
            return fallback;
        }

        throw new InvalidOperationException(
            $"No LLM providers registered. Expected default provider '{defaultProvider}'.");
    }

    /// <inheritdoc />
    public Result<ILlmService> GetByProvider(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return Result.Failure<ILlmService>("Provider name cannot be empty");
        }

        if (_providers.TryGetValue(providerName, out var service))
        {
            return Result.Success(service);
        }

        var available = string.Join(", ", _providers.Keys);
        return Result.Failure<ILlmService>(
            $"LLM provider '{providerName}' not found. Available: [{available}]");
    }

    /// <inheritdoc />
    public Result<ILlmService> GetWithSubagentSupport()
    {
        var subagentCapable = _providers.Values.FirstOrDefault(s => s.SupportsSubagents);

        if (subagentCapable is not null)
        {
            LogSubagentProviderResolved(logger, subagentCapable.ProviderName);
            return Result.Success(subagentCapable);
        }

        return Result.Failure<ILlmService>(
            "No registered LLM provider supports subagent invocations. Register a Claude provider to enable subagents.");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableProviders() => _providers.Keys.ToList();

    [LoggerMessage(EventId = 200, Level = LogLevel.Warning,
        Message = "Default provider '{DefaultProvider}' not found, falling back to '{FallbackProvider}'")]
    private static partial void LogFallingBackToProvider(ILogger logger, string defaultProvider,
        string fallbackProvider);

    [LoggerMessage(EventId = 201, Level = LogLevel.Debug,
        Message = "Resolved subagent-capable provider: {ProviderName}")]
    private static partial void LogSubagentProviderResolved(ILogger logger, string providerName);
}
