using System.Globalization;
using System.Text;
using System.Text.Json;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;

namespace Daedalus.Application.Services.Brainstorm;

/// <summary>
///     Orchestrates interactive brainstorming sessions by coordinating between
///     the user, LLM agent, and persistence layer. Each session progresses through
///     structured phases (context gathering, clarification, proposals, design review,
///     plan generation, and task creation) to produce actionable implementation plans.
/// </summary>
public sealed partial class BrainstormService(
    IBrainstormRepository repository,
    IAgentFactory agentFactory,
    IProjectRepository projectRepository,
    IPrdService prdService,
    ILogger<BrainstormService> logger) : IBrainstormService
{
    /// <summary>How many times a generate saves its completed session before it gives up on lost races.</summary>
    private const int MaxCompletionSaves = 3;

    /// <summary>
    ///     A generate whose completed session could not be saved, for any reason: the tasks exist, so it carries no
    ///     conflict marker and invites no retry, which would create them again (ruling R83).
    /// </summary>
    private static readonly CompositeFormat CompletionNotSaved = CompositeFormat.Parse(
        "Created {0} tasks, but the session could not be marked complete. The tasks exist; do not generate them again.");

    /// <inheritdoc />
    public async Task<Result<BrainstormSession>> CreateSessionAsync(Guid projectId, CancellationToken ct)
    {
        var projectResult = await projectRepository.GetByIdAsync(projectId, ct).ConfigureAwait(false);
        if (projectResult.IsFailure)
            return Result<BrainstormSession>.Failure($"Project not found: {projectResult.Error}");

        var sessionResult = BrainstormSession.Create(projectId);
        if (sessionResult.IsFailure)
            return sessionResult;

        var addResult = await repository.AddAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (addResult.IsFailure)
            return addResult;

        // The save below is guarded by the row version the session was read with, and an add reads none back, so the
        // stored session is read before it is changed (ruling R77).
        var storedResult = await repository.GetByIdAsync(sessionResult.Value.Id, ct).ConfigureAwait(false);
        if (storedResult.IsFailure)
            return storedResult;

        var session = storedResult.Value;
        var project = projectResult.Value;
        var contextPrompt = string.Format(
            CultureInfo.InvariantCulture,
            BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.ContextGathering),
            $"Project: {project.ProjectName}\nDescription: {project.Description}\nVersion: {project.Version}");

        var llmResult = await agentFactory.InvokeAsync(contextPrompt, ct).ConfigureAwait(false);
        if (llmResult.IsFailure)
            return Result<BrainstormSession>.Failure($"LLM invocation failed: {llmResult.Error}");

        session.AddMessage(MessageRole.Assistant, llmResult.Value.Response);
        var saved = await repository.UpdateAsync(session, ct).ConfigureAwait(false);
        if (saved.IsFailure)
            return Result<BrainstormSession>.Failure(saved.Error);

        LogSessionCreated(logger, session.Id, projectId);
        return Result<BrainstormSession>.Success(session);
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
            return Result<BrainstormMessage>.Failure(sessionResult.Error);

        var session = sessionResult.Value;
        var addResult = session.AddMessage(MessageRole.User, userMessage);
        if (addResult.IsFailure)
            return Result<BrainstormMessage>.Failure(addResult.Error);

        var prompt = BuildConversationPrompt(session);
        var llmResult = await agentFactory.InvokeAsync(prompt, ct).ConfigureAwait(false);
        if (llmResult.IsFailure)
            return Result<BrainstormMessage>.Failure($"LLM invocation failed: {llmResult.Error}");

        session.AddMessage(MessageRole.Assistant, llmResult.Value.Response);
        var saved = await repository.UpdateAsync(session, ct).ConfigureAwait(false);
        if (saved.IsFailure)
            return Result<BrainstormMessage>.Failure(saved.Error);

        var lastMessage = session.Messages[^1];
        return Result<BrainstormMessage>.Success(lastMessage);
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
            return Result<BrainstormSession>.Failure(advanceResult.Error);

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

        var saved = await repository.UpdateAsync(session, ct).ConfigureAwait(false);
        if (saved.IsFailure)
            return Result<BrainstormSession>.Failure(saved.Error);

        LogPhaseAdvanced(logger, sessionId, session.Phase);
        return Result<BrainstormSession>.Success(session);
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

        var saved = await repository.UpdateAsync(session, ct).ConfigureAwait(false);
        if (saved.IsFailure)
            return Result.Failure(saved.Error);

        LogSessionAbandoned(logger, sessionId);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<List<TaskDto>>> GenerateTasksAsync(Guid sessionId, CancellationToken ct)
    {
        var sessionResult = await repository.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return Result<List<TaskDto>>.Failure(sessionResult.Error);

        var session = sessionResult.Value;

        if (session.Phase != BrainstormPhase.TaskCreation)
            return Result<List<TaskDto>>.Failure(
                $"Session must be in TaskCreation phase to generate tasks. Current phase: {session.Phase}");

        if (string.IsNullOrWhiteSpace(session.ImplementationPlan))
            return Result<List<TaskDto>>.Failure("Session has no implementation plan to convert to tasks.");

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
            return Result<List<TaskDto>>.Failure($"LLM invocation failed: {llmResult.Error}");

        List<PrdItemForConversionDto> items;
        try
        {
            var json = llmResult.Value.Response.Trim();
            items = JsonSerializer.Deserialize(json, BrainstormJsonContext.Default.ListPrdItemForConversionDto)
                    ?? [];

            if (items.Count == 0)
                return Result<List<TaskDto>>.Failure("LLM returned an empty task list.");
        }
        catch (JsonException ex)
        {
            return Result<List<TaskDto>>.Failure($"Failed to parse LLM response as task items: {ex.Message}");
        }

        var tasksResult = await prdService.ConvertToTasksAsync(session.ProjectId, items, ct).ConfigureAwait(false);
        if (tasksResult.IsFailure)
            return Result<List<TaskDto>>.Failure($"Task conversion failed: {tasksResult.Error}");

        var tasks = tasksResult.Value;
        var completed = MarkTasksGenerated(session, tasks.Count);
        var saved = completed.IsFailure
            ? completed
            : await repository.UpdateAsync(session, ct).ConfigureAwait(false);

        // The tasks now exist, committed on their own, so a retry of the whole generate would create them again. A save
        // that lost a race is redone on the session as read again, while it is still in TaskCreation: marking it
        // complete does not depend on what the other write changed (ruling R83).
        for (var attempt = 1;
             saved.IsFailure && SaveConflict.IsConflict(saved.Error) && attempt < MaxCompletionSaves;
             attempt++)
        {
            var reread = await repository.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
            if (reread.IsFailure || reread.Value.Phase != BrainstormPhase.TaskCreation
                || MarkTasksGenerated(reread.Value, tasks.Count).IsFailure)
                break;

            saved = await repository.UpdateAsync(reread.Value, ct).ConfigureAwait(false);
        }

        if (saved.IsFailure)
        {
            // Every failure after the tasks exist answers one text; the reason is logged, not echoed, since its text
            // may invite a retry, which would create the tasks again (ruling R83).
            LogTasksGeneratedSessionNotSaved(logger, sessionId, tasks.Count, saved.Error);
            return Result<List<TaskDto>>.Failure(
                string.Format(CultureInfo.InvariantCulture, CompletionNotSaved, tasks.Count));
        }

        LogTasksGenerated(logger, sessionId, tasks.Count);
        return Result<List<TaskDto>>.Success(tasks);
    }

    /// <summary>
    ///     Records on <paramref name="session"/> that its tasks were generated: a system message with the count, the
    ///     assistant's completion message, and the advance from TaskCreation to Completed.
    /// </summary>
    private static Result MarkTasksGenerated(BrainstormSession session, int taskCount)
    {
        var systemMsgResult = session.AddMessage(MessageRole.System,
            string.Format(CultureInfo.InvariantCulture, "Generated {0} tasks from implementation plan.", taskCount));
        if (systemMsgResult.IsFailure)
            return systemMsgResult;

        var completeMsgResult = session.AddMessage(MessageRole.Assistant, "Task generation complete. [PHASE_COMPLETE]");
        if (completeMsgResult.IsFailure)
            return completeMsgResult;

        return session.AdvancePhase();
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

    [LoggerMessage(EventId = 204, Level = LogLevel.Warning,
        Message = "Brainstorm session {SessionId} generated {TaskCount} tasks but could not be marked complete: {Reason}")]
    private static partial void LogTasksGeneratedSessionNotSaved(
        ILogger logger, Guid sessionId, int taskCount, string reason);
}
