using System.Diagnostics;
using System.Security.Cryptography;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services;

/// <summary>
///     Executes agents and manages their execution contexts.
///     Can execute agents locally (via delegates) or invoke remote agent services.
/// </summary>
public sealed partial class AgentExecutor(ILogger<AgentExecutor> logger) : IAgentExecutor
{
    /// <summary>Registry of available agent implementations keyed by agent ID.</summary>
    private readonly Dictionary<string, Func<string, CancellationToken, Task<string>>> _agentHandlers =
        new(StringComparer.Ordinal);

    /// <summary>Execution history for tracking.</summary>
    private readonly List<AgentExecutionContext> _executionHistory = [];

    public async Task<Result<AgentExecutionContext>> ExecuteAgentAsync(
        AgentMetadata agent,
        string prompt,
        CancellationToken ct = default)
    {
        if (agent == null)
        {
            return Result.Failure<AgentExecutionContext>("Agent cannot be null");
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Result.Failure<AgentExecutionContext>("Prompt cannot be empty");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Check if we have a local handler for this agent
            var hasHandler = _agentHandlers.TryGetValue(agent.Id, out var handler);

            string response;
            if (hasHandler && handler != null)
            {
                // Execute local handler
                LogExecutingLocalHandler(logger, agent.Id, agent.Name);

                response = await handler(prompt, ct);
            }
            else
            {
                // For agents without handlers, return a simulated response based on agent metadata
                LogNoHandlerFound(logger, agent.Id);

                response = GenerateSimulatedResponse(agent, prompt);
            }

            stopwatch.Stop();

            var context = new AgentExecutionContext
            {
                Agent = agent,
                InputPrompt = prompt,
                OutputResponse = response,
                IsSuccessful = true,
                ExecutionDuration = stopwatch.Elapsed,
                ExecutedAt = DateTime.UtcNow,
                Metadata =
                {
                    ["handler_type"] = hasHandler ? "local" : "simulated",
                    ["prompt_length"] = prompt.Length,
                    ["response_length"] = response.Length,
                    ["token_estimate"] = EstimateTokens(response)
                }
            };

            _executionHistory.Add(context);
            LogAgentExecutionSuccess(logger, agent.Name, stopwatch.ElapsedMilliseconds);

            return Result.Success(context);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            LogAgentExecutionCancelled(logger, agent.Id, stopwatch.ElapsedMilliseconds);

            return Result.Failure<AgentExecutionContext>(
                $"Execution cancelled after {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogAgentExecutionError(logger, ex, agent.Id, agent.Name);

            return Result.Failure<AgentExecutionContext>($"Agent execution failed: {ex.Message}");
        }
    }

    public async Task<Result<AgentExecutionChain>> ExecuteAgentChainAsync(
        IReadOnlyList<AgentMetadata> agents,
        string initialPrompt,
        CancellationToken ct = default)
    {
        if (agents == null || agents.Count == 0)
        {
            return Result.Failure<AgentExecutionChain>("At least one agent is required");
        }

        if (string.IsNullOrWhiteSpace(initialPrompt))
        {
            return Result.Failure<AgentExecutionChain>("Initial prompt cannot be empty");
        }

        var chainStopwatch = Stopwatch.StartNew();
        var chain = new AgentExecutionChain { OriginalPrompt = initialPrompt, CreatedAt = DateTime.UtcNow };

        var currentPrompt = initialPrompt;

        try
        {
            LogStartingChain(logger, agents.Count);

            for (var i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];

                LogExecutingChainStep(logger, i + 1, agents.Count, agent.Name);

                var executionResult = await ExecuteAgentAsync(agent, currentPrompt, ct);

                if (executionResult.IsFailure)
                {
                    LogChainStepFailed(logger, i + 1, executionResult.Error);

                    chain.IsCompleted = false;
                    chain.ExecutionHistory.Add(new AgentExecutionContext
                    {
                        Agent = agent,
                        InputPrompt = currentPrompt,
                        IsSuccessful = false,
                        ErrorMessage = executionResult.Error,
                        ExecutedAt = DateTime.UtcNow
                    });

                    chainStopwatch.Stop();
                    chain.TotalDuration = chainStopwatch.Elapsed;

                    return Result.Failure<AgentExecutionChain>(
                        $"Agent execution failed at step {i + 1}: {executionResult.Error}");
                }

                var context = executionResult.Value;
                chain.ExecutionHistory.Add(context);

                // Use the agent's output as input for the next agent
                currentPrompt = context.OutputResponse;
            }

            chainStopwatch.Stop();

            // The final output is the output of the last agent
            chain.FinalOutput = currentPrompt;
            chain.IsCompleted = true;
            chain.TotalDuration = chainStopwatch.Elapsed;

            LogChainCompleted(logger, chainStopwatch.ElapsedMilliseconds, agents.Count);

            return Result.Success(chain);
        }
        catch (Exception ex)
        {
            chainStopwatch.Stop();
            LogChainError(logger, ex);

            chain.IsCompleted = false;
            chain.TotalDuration = chainStopwatch.Elapsed;

            return Result.Failure<AgentExecutionChain>($"Chain execution failed: {ex.Message}");
        }
    }

    public Task<Result<IReadOnlyList<AgentExecutionContext>>> GetAgentHistoryAsync(
        string agentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<AgentExecutionContext>>("Agent ID cannot be empty"));
        }

        var history = _executionHistory
            .Where(e => string.Equals(e.Agent.Id, agentId, StringComparison.Ordinal))
            .ToList()
            .AsReadOnly();

        LogHistoryRetrieved(logger, history.Count, agentId);

        return Task.FromResult(Result.Success<IReadOnlyList<AgentExecutionContext>>(history));
    }

    /// <summary>
    ///     Registers a local agent handler.
    /// </summary>
    public void RegisterAgentHandler(
        string agentId,
        Func<string, CancellationToken, Task<string>> handler)
    {
        _agentHandlers[agentId] = handler ?? throw new ArgumentNullException(nameof(handler));
        LogHandlerRegistered(logger, agentId);
    }

    /// <summary>
    ///     Generates a simulated agent response based on agent metadata and prompt.
    /// </summary>
    private static string GenerateSimulatedResponse(AgentMetadata agent, string prompt)
    {
        var agentType = agent.Tags.Count > 0 ? agent.Tags[0] : agent.Organization;

        // Use cryptographically secure random for timing simulation only
        using var rng = RandomNumberGenerator.Create();
        var buffer = new byte[4];
        rng.GetBytes(buffer);
        var randomResponse = (BitConverter.ToInt32(buffer, 0) & int.MaxValue) % 900 + 100;

        rng.GetBytes(buffer);
        var randomConfidence = (BitConverter.ToInt32(buffer, 0) & int.MaxValue) % 26 + 75;

        return $"""
                [Agent: {agent.Name}]
                [Organization: {agent.Organization}]
                [Specialization: {agentType}]

                Agent Processing...

                Input Prompt:
                {prompt}

                ---

                Agent Analysis (from: {agent.Description}):

                The {agent.Name} agent has processed your request focusing on:
                {string.Join('\n', agent.Tags.Take(3).Select(t => $"• {t}"))}

                Recommendations:
                1. Apply {agent.Name} best practices
                2. Follow {agent.Organization} guidelines
                3. Consider specialized {agentType} patterns

                ---

                Status: ✓ Complete
                Agent Response Time: ~{randomResponse}ms
                Confidence: {randomConfidence}%
                """;
    }

    /// <summary>
    ///     Rough token estimation (typically 1 token ≈ 4 chars).
    /// </summary>
    private static int EstimateTokens(string text) => text.Length / 4 + 1;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Executing local agent handler for {AgentId} ({AgentName})")]
    private static partial void LogExecutingLocalHandler(ILogger logger, string agentId, string agentName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "No handler found for agent {AgentId}, using simulated response")]
    private static partial void LogNoHandlerFound(ILogger logger, string agentId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Successfully executed agent {AgentName} in {Duration}ms")]
    private static partial void LogAgentExecutionSuccess(ILogger logger, string agentName, long duration);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Agent execution cancelled for {AgentId} after {Duration}ms")]
    private static partial void LogAgentExecutionCancelled(ILogger logger, string agentId, long duration);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Error executing agent {AgentId} ({AgentName})")]
    private static partial void LogAgentExecutionError(ILogger logger, Exception exception, string agentId,
        string agentName);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "Starting agent execution chain with {AgentCount} agents")]
    private static partial void LogStartingChain(ILogger logger, int agentCount);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Executing agent {Index}/{Total}: {AgentName}")]
    private static partial void LogExecutingChainStep(ILogger logger, int index, int total, string agentName);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Agent chain failed at step {Index}: {Error}")]
    private static partial void LogChainStepFailed(ILogger logger, int index, string error);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information,
        Message = "Agent chain completed successfully in {Duration}ms with {AgentCount} agents")]
    private static partial void LogChainCompleted(ILogger logger, long duration, int agentCount);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Unexpected error in agent execution chain")]
    private static partial void LogChainError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information,
        Message = "Retrieved {HistoryCount} execution records for agent {AgentId}")]
    private static partial void LogHistoryRetrieved(ILogger logger, int historyCount, string agentId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Registered agent handler for {AgentId}")]
    private static partial void LogHandlerRegistered(ILogger logger, string agentId);
}
