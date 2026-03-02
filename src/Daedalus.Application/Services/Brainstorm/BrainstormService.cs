#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services.Brainstorm;

/// <summary>
///     Orchestrates interactive brainstorming sessions by coordinating between
///     the user, LLM agent, and persistence layer. Each session progresses through
///     structured phases (context gathering, clarification, proposals, design review,
///     plan generation, and task creation) to produce actionable implementation plans.
/// </summary>
public sealed partial class BrainstormService(
    IBrainstormRepository repository,
    IRalphAgentFactory agentFactory,
    IProjectRepository projectRepository,
    IPrdService prdService,
    ILogger<BrainstormService> logger) : IBrainstormService
{
    private static readonly JsonSerializerOptions TaskParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    public async Task<Result<BrainstormSession>> CreateSessionAsync(Guid projectId, CancellationToken ct)
    {
        var projectResult = await projectRepository.GetByIdAsync(projectId, ct).ConfigureAwait(false);
        if (projectResult.IsFailure)
            return Result.Failure<BrainstormSession>($"Project not found: {projectResult.Error}");

        var sessionResult = BrainstormSession.Create(projectId);
        if (sessionResult.IsFailure)
            return sessionResult;

        var session = sessionResult.Value;
        var addResult = await repository.AddAsync(session, ct).ConfigureAwait(false);
        if (addResult.IsFailure)
            return addResult;

        var project = projectResult.Value;
        var contextPrompt = string.Format(
            CultureInfo.InvariantCulture,
            BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.ContextGathering),
            $"Project: {project.ProjectName}\nDescription: {project.Description}\nVersion: {project.Version}");

        var llmResult = await agentFactory.InvokeAsync(contextPrompt, ct).ConfigureAwait(false);
        if (llmResult.IsFailure)
            return Result.Failure<BrainstormSession>($"LLM invocation failed: {llmResult.Error}");

        session.AddMessage(MessageRole.Assistant, llmResult.Value.Response);
        await repository.UpdateAsync(session, ct).ConfigureAwait(false);

        LogSessionCreated(logger, session.Id, projectId);
        return Result.Success(session);
    }

    /// <inheritdoc />
    public async Task<Result<BrainstormSession>> GetSessionAsync(Guid sessionId, CancellationToken ct)
    {
        return await repository.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrainstormSession>>> GetSessionsByProjectAsync(
        Guid projectId, CancellationToken ct)
    {
        return await repository.GetByProjectIdAsync(projectId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<BrainstormMessage>> SendMessageAsync(
        Guid sessionId, string userMessage, CancellationToken ct)
    {
        var sessionResult = await repository.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return Result.Failure<BrainstormMessage>(sessionResult.Error);

        var session = sessionResult.Value;
        var addResult = session.AddMessage(MessageRole.User, userMessage);
        if (addResult.IsFailure)
            return Result.Failure<BrainstormMessage>(addResult.Error);

        var prompt = BuildConversationPrompt(session);
        var llmResult = await agentFactory.InvokeAsync(prompt, ct).ConfigureAwait(false);
        if (llmResult.IsFailure)
            return Result.Failure<BrainstormMessage>($"LLM invocation failed: {llmResult.Error}");

        session.AddMessage(MessageRole.Assistant, llmResult.Value.Response);
        await repository.UpdateAsync(session, ct).ConfigureAwait(false);

        var lastMessage = session.Messages[^1];
        return Result.Success(lastMessage);
    }

    /// <inheritdoc />
    public async Task<Result<BrainstormSession>> AdvancePhaseAsync(Guid sessionId, CancellationToken ct)
    {
        var sessionResult = await repository.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return sessionResult;

        var session = sessionResult.Value;
        var advanceResult = session.AdvancePhase();
        if (advanceResult.IsFailure)
            return Result.Failure<BrainstormSession>(advanceResult.Error);

        var systemPrompt = BrainstormPromptTemplates.GetSystemPrompt(session.Phase);
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            var prompt = BuildConversationPrompt(session);
            var llmResult = await agentFactory.InvokeAsync(prompt, ct).ConfigureAwait(false);
            if (llmResult.IsSuccess)
            {
                session.AddMessage(MessageRole.Assistant, llmResult.Value.Response);
            }
        }

        await repository.UpdateAsync(session, ct).ConfigureAwait(false);

        LogPhaseAdvanced(logger, sessionId, session.Phase);
        return Result.Success(session);
    }

    /// <inheritdoc />
    public async Task<Result> AbandonSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var sessionResult = await repository.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return Result.Failure(sessionResult.Error);

        var session = sessionResult.Value;
        var abandonResult = session.Abandon();
        if (abandonResult.IsFailure)
            return abandonResult;

        await repository.UpdateAsync(session, ct).ConfigureAwait(false);

        LogSessionAbandoned(logger, sessionId);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<List<TaskDto>>> GenerateTasksAsync(Guid sessionId, CancellationToken ct)
    {
        var sessionResult = await repository.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return Result.Failure<List<TaskDto>>(sessionResult.Error);

        var session = sessionResult.Value;

        if (session.Phase != BrainstormPhase.TaskCreation)
            return Result.Failure<List<TaskDto>>(
                $"Session must be in TaskCreation phase to generate tasks. Current phase: {session.Phase}");

        if (string.IsNullOrWhiteSpace(session.ImplementationPlan))
            return Result.Failure<List<TaskDto>>("Session has no implementation plan to convert to tasks.");

        var parsePrompt = string.Format(
            CultureInfo.InvariantCulture,
            """
            Parse the following implementation plan into a JSON array of task items.
            Each item must have these fields:
            - title (string): short task title
            - description (string): detailed description
            - acceptanceCriteria (string): criteria for completion
            - priority (string): one of "Low", "Medium", "High", "Critical"
            - complexity (string): one of "Low", "Medium", "High"
            - dependencies (string[]): list of dependency task titles
            - phase (string): implementation phase name
            - parallelGroup (int): group number for parallel execution

            Return ONLY a valid JSON array, no markdown fencing, no explanation.

            Implementation plan:
            {0}
            """,
            session.ImplementationPlan);

        var llmResult = await agentFactory.InvokeAsync(parsePrompt, ct).ConfigureAwait(false);
        if (llmResult.IsFailure)
            return Result.Failure<List<TaskDto>>($"LLM invocation failed: {llmResult.Error}");

        List<PrdItemForConversionDto> items;
        try
        {
            var json = llmResult.Value.Response.Trim();
            items = JsonSerializer.Deserialize<List<PrdItemForConversionDto>>(json, TaskParseOptions)
                    ?? [];

            if (items.Count == 0)
                return Result.Failure<List<TaskDto>>("LLM returned an empty task list.");
        }
        catch (JsonException ex)
        {
            return Result.Failure<List<TaskDto>>($"Failed to parse LLM response as task items: {ex.Message}");
        }

        var tasksResult = await prdService.ConvertToTasksAsync(session.ProjectId, items, ct).ConfigureAwait(false);
        if (tasksResult.IsFailure)
            return Result.Failure<List<TaskDto>>($"Task conversion failed: {tasksResult.Error}");

        var systemMsgResult = session.AddMessage(MessageRole.System,
            string.Format(CultureInfo.InvariantCulture,
                "Generated {0} tasks from implementation plan.", tasksResult.Value.Count));
        if (systemMsgResult.IsFailure)
            return Result.Failure<List<TaskDto>>(systemMsgResult.Error);

        var completeMsgResult = session.AddMessage(MessageRole.Assistant, "Task generation complete. [PHASE_COMPLETE]");
        if (completeMsgResult.IsFailure)
            return Result.Failure<List<TaskDto>>(completeMsgResult.Error);

        var advanceResult = session.AdvancePhase();
        if (advanceResult.IsFailure)
            return Result.Failure<List<TaskDto>>(advanceResult.Error);

        await repository.UpdateAsync(session, ct).ConfigureAwait(false);

        LogTasksGenerated(logger, sessionId, tasksResult.Value.Count);
        return Result.Success(tasksResult.Value);
    }

    private static string BuildConversationPrompt(BrainstormSession session)
    {
        var sb = new StringBuilder();

        var systemPrompt = BrainstormPromptTemplates.GetSystemPrompt(session.Phase);
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            sb.AppendLine(systemPrompt);
            sb.AppendLine();
        }

        sb.AppendLine("=== CONVERSATION HISTORY ===");
        foreach (var message in session.Messages)
        {
            var roleLabel = message.Role switch
            {
                MessageRole.System => "SYSTEM",
                MessageRole.Assistant => "ASSISTANT",
                MessageRole.User => "USER",
                _ => "UNKNOWN"
            };
            sb.AppendLine(CultureInfo.InvariantCulture, $"[{roleLabel}]: {message.Content}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Information,
        Message = "Brainstorm session {SessionId} created for project {ProjectId}")]
    private static partial void LogSessionCreated(ILogger logger, Guid sessionId, Guid projectId);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information,
        Message = "Brainstorm session {SessionId} advanced to phase {Phase}")]
    private static partial void LogPhaseAdvanced(ILogger logger, Guid sessionId, BrainstormPhase phase);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information,
        Message = "Brainstorm session {SessionId} abandoned")]
    private static partial void LogSessionAbandoned(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 203, Level = LogLevel.Information,
        Message = "Brainstorm session {SessionId} generated {TaskCount} tasks from implementation plan")]
    private static partial void LogTasksGenerated(ILogger logger, Guid sessionId, int taskCount);
}
