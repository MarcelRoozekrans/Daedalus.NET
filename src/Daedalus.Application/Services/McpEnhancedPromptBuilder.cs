using System.Globalization;
using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services;

/// <summary>
///     Enhanced prompt builder that integrates MCP agent selection
///     to automatically recommend and apply the best agent for each prompt,
///     and injects up-to-date Context7 documentation for library APIs.
/// </summary>
public sealed class McpEnhancedPromptBuilder(
    DefaultPromptBuilder innerBuilder,
    IMcpAgentSelector agentSelector,
    IContext7DocumentationInjector context7Injector,
    ILogger<McpEnhancedPromptBuilder> logger) : IPromptBuilder
{
    /// <summary>Metadata key for storing the selected agent ID.</summary>
    private const string _selectedAgentIdKey = "mcp_selected_agent_id";

    /// <summary>Metadata key for storing the agent relevance score.</summary>
    private const string _agentRelevanceScoreKey = "mcp_agent_relevance_score";

    /// <summary>Metadata key for storing applied agent instructions.</summary>
    private const string _appliedAgentInstructionsKey = "mcp_applied_agent_instructions";

    /// <summary>Metadata key for indicating Context7 documentation was injected.</summary>
    private const string _context7InjectedKey = "context7_documentation_injected";

    /// <summary>Maximum estimated prompt size (in characters) before logging a warning.</summary>
    private const int _maxEstimatedPromptSize = 8000;

    /// <summary>Maximum estimated prompt size (in characters) before truncating optional enhancements.</summary>
    private const int _maxHardPromptLimit = 12000;

    /// <summary>Maximum number of focus areas from agent guidance to include.</summary>
    private const int _maxAgentFocusAreas = 3;

    /// <summary>Maximum number of agent reference URLs to include.</summary>
    private const int _maxAgentReferences = 1;

    public async Task<Result<PromptContext>> InitializeContextAsync(
        Guid taskId,
        Guid sessionId,
        string originalPrompt,
        string completionPromise,
        string? learnings,
        CancellationToken ct)
    {
        // Initialize using the base builder
        var contextResult = await innerBuilder.InitializeContextAsync(
            taskId, sessionId, originalPrompt, completionPromise, learnings, ct);

        if (contextResult.IsFailure)
        {
            return contextResult;
        }

        var context = contextResult.Value;

        // Find the best agent for this prompt
        var agentResult = await agentSelector.FindAgentsForPromptAsync(
            originalPrompt, maxResults: 1, ct: ct);

        if (agentResult.IsSuccess && agentResult.Value.Count > 0)
        {
            var bestAgent = agentResult.Value[0];
            context.Metadata[_selectedAgentIdKey] = bestAgent.Id;
            context.Metadata[_agentRelevanceScoreKey] = bestAgent.RelevanceScore.ToString(CultureInfo.InvariantCulture);
        }

        return Result.Success(context);
    }

    public async Task<Result<string>> BuildIterationPromptAsync(
        PromptContext context,
        CancellationToken ct)
    {
        // Build the prompt using the base builder
        var promptResult = await innerBuilder.BuildIterationPromptAsync(context, ct);

        if (promptResult.IsFailure)
        {
            return promptResult;
        }

        var basePrompt = promptResult.Value;
        var enhancedPrompt = basePrompt;

        // Check if we have room for optional enhancements (agent + Context7 documentation)
        var canAddEnhancements = basePrompt.Length < _maxEstimatedPromptSize;

        if (!canAddEnhancements)
        {
            logger.LogWarning(
                "Prompt size ({PromptSize} chars) approaching limit ({MaxSize}). " +
                "Skipping optional MCP enhancements (agent guidance and Context7 docs).",
                basePrompt.Length,
                _maxEstimatedPromptSize);
            return Result.Success(enhancedPrompt);
        }

        // Inject agent-specific guidance INTO the existing "GUIDANCE FOR THIS ITERATION" section
        if (context.Metadata.TryGetValue(_selectedAgentIdKey, out var agentId))
        {
            var agentResult = await agentSelector.GetAgentAsync(agentId, ct);

            if (agentResult.IsSuccess)
            {
                var agent = agentResult.Value;
                var agentGuidance = InjectAgentGuidanceIntoPrompt(enhancedPrompt, agent);

                // Only apply if it doesn't exceed hard limit
                if (agentGuidance.Length <= _maxHardPromptLimit)
                {
                    enhancedPrompt = agentGuidance;
                    context.Metadata[_appliedAgentInstructionsKey] = agent.Name;
                }
            }
        }

        // Inject Context7 documentation if we still have room
        if (enhancedPrompt.Length < _maxEstimatedPromptSize)
        {
            var context7Result = await context7Injector.GetDocumentationContextAsync(
                context.OriginalPrompt, ct);

            if (context7Result.IsSuccess && !string.IsNullOrEmpty(context7Result.Value))
            {
                var docContextLength = context7Result.Value.Length;

                // Only inject if we won't exceed hard limit
                if (enhancedPrompt.Length + docContextLength <= _maxHardPromptLimit)
                {
                    enhancedPrompt = InjectContext7DocumentationIntoPrompt(enhancedPrompt, context7Result.Value);
                    context.Metadata[_context7InjectedKey] = "true";
                }
                else
                {
                    logger.LogWarning(
                        "Context7 documentation skipped: would exceed {MaxLimit} char limit. " +
                        "Current: {CurrentSize}, Doc size: {DocSize}",
                        _maxHardPromptLimit,
                        enhancedPrompt.Length,
                        docContextLength);
                }
            }
        }

        return Result.Success(enhancedPrompt);
    }

    public async Task<Result> RecordIterationResultAsync(
        PromptContext context,
        int iterationNumber,
        string prompt,
        string response,
        bool completionPromiseFound,
        TimeSpan duration,
        string? executionError,
        CancellationToken ct)
    {
        // Delegate to the base builder
        return await innerBuilder.RecordIterationResultAsync(
            context, iterationNumber, prompt, response,
            completionPromiseFound, duration, executionError, ct);
    }

    public async Task<Result> PersistContextAsync(
        PromptContext context,
        CancellationToken ct)
    {
        // Delegate to the base builder
        return await innerBuilder.PersistContextAsync(context, ct);
    }

    public async Task<Result<PromptContext>> LoadContextAsync(
        Guid taskId,
        Guid sessionId,
        CancellationToken ct)
    {
        // Delegate to the base builder
        return await innerBuilder.LoadContextAsync(taskId, sessionId, ct);
    }

    /// <summary>
    ///     Injects agent-specific guidance INTO the existing "GUIDANCE FOR THIS ITERATION" section
    ///     to preserve the learning loop as the primary signal while enhancing with agent expertise.
    /// </summary>
    private static string InjectAgentGuidanceIntoPrompt(string basePrompt, AgentMetadata agent)
    {
        // Find the "=== GUIDANCE FOR THIS ITERATION ===" section and inject agent guidance there
        const string guidanceMarker = "=== GUIDANCE FOR THIS ITERATION ===";

        var guidanceIndex = basePrompt.IndexOf(guidanceMarker, StringComparison.Ordinal);
        if (guidanceIndex < 0)
        {
            // If no guidance section exists (iteration 1), append agent guidance at the end
            return $"{basePrompt}\n\n{BuildAgentGuidanceSummary(agent)}";
        }

        // Insert agent guidance right after the guidance marker, before existing guidance
        var insertPosition = guidanceIndex + guidanceMarker.Length + Environment.NewLine.Length;
        var agentGuidance = BuildAgentGuidanceSummary(agent);

        return basePrompt.Insert(insertPosition, agentGuidance + Environment.NewLine);
    }

    /// <summary>
    ///     Builds a concise agent guidance summary for injection into the iteration prompt.
    ///     Focuses on actionable next steps based on agent expertise.
    /// </summary>
#pragma warning disable CA1305, MA0011 // Locale-aware formatting not needed for LLM prompts
    private static string BuildAgentGuidanceSummary(AgentMetadata agent)
    {
        var sb = new StringBuilder();

        // Confidence indicator
        var confidenceLevel = agent.RelevanceScore switch
        {
            >= 85 => "HIGHLY RELEVANT",
            >= 70 => "RELEVANT",
            >= 50 => "POTENTIALLY RELEVANT",
            _ => "AVAILABLE"
        };
        sb.AppendLine($"🎯 Agent Context ({confidenceLevel} - {agent.Name} [{agent.Organization}]):");

        // Agent-specific focus areas
        var focusAreas = GenerateFocusAreasFromTags(agent.Tags);
        sb.AppendLine("   Next iteration, prioritize:");
        foreach (var area in focusAreas.Take(_maxAgentFocusAreas)) // Limit to configured max
        {
            sb.AppendLine($"   • {area}");
        }

        if (agent.SourceUrl is not null && _maxAgentReferences > 0)
        {
            sb.AppendLine($"   📖 Reference: {agent.SourceUrl}");
        }

        sb.AppendLine();
        return sb.ToString();
    }
#pragma warning restore CA1305, MA0011

    /// <summary>
    ///     Injects Context7 documentation into the prompt, placed before the task section
    ///     so the LLM has library documentation context when working on the solution.
    /// </summary>
    private static string InjectContext7DocumentationIntoPrompt(string basePrompt, string documentation)
    {
        // Find the "=== YOUR TASK ===" section and inject documentation before it
        const string taskMarker = "=== YOUR TASK ===";

        var taskIndex = basePrompt.IndexOf(taskMarker, StringComparison.Ordinal);
        if (taskIndex < 0)
        {
            // If no task section, append before the end
            return $"{basePrompt}\n\n{documentation}";
        }

        // Insert documentation right before the task marker
        return basePrompt.Insert(taskIndex, documentation + Environment.NewLine);
    }

    /// <summary>
    ///     Generates focus areas from agent tags, mapping them to 8 domain categories.
    /// </summary>
#pragma warning disable CA1307 // Specify StringComparison for clarity - using ordinal case-insensitive substring matching
    private static List<string> GenerateFocusAreasFromTags(IReadOnlyList<string> tags)
    {
        var tagLower = tags.Select(t => t.ToUpperInvariant()).ToList();
        var focusAreas = new List<string>();

        // Architecture & Design
        if (tagLower.Exists(t =>
                t.Contains("ARCHITECTURE", StringComparison.Ordinal) ||
                t.Contains("DESIGN", StringComparison.Ordinal) || t.Contains("PATTERN", StringComparison.Ordinal)))
        {
            focusAreas.Add("Design: Ensure architecture follows established patterns and is maintainable");
        }

        // Performance & Optimization
        if (tagLower.Exists(t =>
                t.Contains("PERFORMANCE", StringComparison.Ordinal) ||
                t.Contains("OPTIMIZATION", StringComparison.Ordinal) ||
                t.Contains("EFFICIENCY", StringComparison.Ordinal)))
        {
            focusAreas.Add("Performance: Optimize for speed, memory efficiency, and scalability");
        }

        // Security & Compliance
        if (tagLower.Exists(t =>
                t.Contains("SECURITY", StringComparison.Ordinal) ||
                t.Contains("COMPLIANCE", StringComparison.Ordinal) ||
                t.Contains("VULNERABILITY", StringComparison.Ordinal)))
        {
            focusAreas.Add("Security: Apply security best practices and vulnerability prevention");
        }

        // Testing & Quality
        if (tagLower.Exists(t =>
                t.Contains("TEST", StringComparison.Ordinal) || t.Contains("QUALITY", StringComparison.Ordinal) ||
                t.Contains("COVERAGE", StringComparison.Ordinal)))
        {
            focusAreas.Add("Testing: Ensure comprehensive test coverage and code quality");
        }

        // DevOps & Infrastructure
        if (tagLower.Exists(t =>
                t.Contains("DEVOPS", StringComparison.Ordinal) ||
                t.Contains("INFRASTRUCTURE", StringComparison.Ordinal) ||
                t.Contains("DEPLOYMENT", StringComparison.Ordinal)))
        {
            focusAreas.Add("Infrastructure: Consider deployment, automation, and operational aspects");
        }

        // Database & Data
        if (tagLower.Exists(t =>
                t.Contains("DATABASE", StringComparison.Ordinal) || t.Contains("DATA", StringComparison.Ordinal) ||
                t.Contains("QUERY", StringComparison.Ordinal)))
        {
            focusAreas.Add("Data: Optimize queries, schema design, and data integrity");
        }

        // Code Style & Standards
        if (tagLower.Exists(t =>
                t.Contains("CODE", StringComparison.Ordinal) || t.Contains("STYLE", StringComparison.Ordinal) ||
                t.Contains("STANDARD", StringComparison.Ordinal)))
        {
            focusAreas.Add("Code Quality: Follow naming conventions, readability, and team standards");
        }

        // Refactoring & Maintenance
        if (tagLower.Exists(t =>
                t.Contains("REFACTOR", StringComparison.Ordinal) || t.Contains("MAINTAIN", StringComparison.Ordinal) ||
                t.Contains("LEGACY", StringComparison.Ordinal)))
        {
            focusAreas.Add("Maintainability: Improve code clarity, reduce technical debt, simplify logic");
        }

        // Default fallback if no tags matched
        if (focusAreas.Count == 0)
        {
            focusAreas.Add("General: Apply this agent's specialized expertise to improve your solution");
        }

        return focusAreas;
    }
#pragma warning restore CA1307
}
