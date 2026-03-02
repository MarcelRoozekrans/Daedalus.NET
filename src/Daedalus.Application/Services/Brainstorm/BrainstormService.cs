using System.Globalization;
using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
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
    ILogger<BrainstormService> logger) : IBrainstormService
{
    /// <inheritdoc />
    public async Task<Result<BrainstormSession>> CreateSessionAsync(Guid projectId, CancellationToken ct)
    {
        var projectResult = await projectRepository.GetByIdAsync(projectId, ct);
        if (projectResult.IsFailure)
            return Result.Failure<BrainstormSession>($"Project not found: {projectResult.Error}");

        var sessionResult = BrainstormSession.Create(projectId);
        if (sessionResult.IsFailure)
            return sessionResult;

        var session = sessionResult.Value;
        var addResult = await repository.AddAsync(session, ct);
        if (addResult.IsFailure)
            return addResult;

        var project = projectResult.Value;
        var contextPrompt = string.Format(
            CultureInfo.InvariantCulture,
            BrainstormPromptTemplates.GetSystemPrompt(BrainstormPhase.ContextGathering),
            $"Project: {project.ProjectName}\nDescription: {project.Description}\nVersion: {project.Version}");

        var llmResult = await agentFactory.InvokeAsync(contextPrompt, ct);
        if (llmResult.IsFailure)
            return Result.Failure<BrainstormSession>($"LLM invocation failed: {llmResult.Error}");

        session.AddMessage(MessageRole.Assistant, llmResult.Value.Response);
        await repository.UpdateAsync(session, ct);

        LogSessionCreated(logger, session.Id, projectId);
        return Result.Success(session);
    }

    /// <inheritdoc />
    public async Task<Result<BrainstormSession>> GetSessionAsync(Guid sessionId, CancellationToken ct)
    {
        return await repository.GetByIdAsync(sessionId, ct);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrainstormSession>>> GetSessionsByProjectAsync(
        Guid projectId, CancellationToken ct)
    {
        return await repository.GetByProjectIdAsync(projectId, ct);
    }

    /// <inheritdoc />
    public async Task<Result<BrainstormMessage>> SendMessageAsync(
        Guid sessionId, string userMessage, CancellationToken ct)
    {
        var sessionResult = await repository.GetByIdAsync(sessionId, ct);
        if (sessionResult.IsFailure)
            return Result.Failure<BrainstormMessage>(sessionResult.Error);

        var session = sessionResult.Value;
        var addResult = session.AddMessage(MessageRole.User, userMessage);
        if (addResult.IsFailure)
            return Result.Failure<BrainstormMessage>(addResult.Error);

        var prompt = BuildConversationPrompt(session);
        var llmResult = await agentFactory.InvokeAsync(prompt, ct);
        if (llmResult.IsFailure)
            return Result.Failure<BrainstormMessage>($"LLM invocation failed: {llmResult.Error}");

        session.AddMessage(MessageRole.Assistant, llmResult.Value.Response);
        await repository.UpdateAsync(session, ct);

        var lastMessage = session.Messages[^1];
        return Result.Success(lastMessage);
    }

    /// <inheritdoc />
    public async Task<Result<BrainstormSession>> AdvancePhaseAsync(Guid sessionId, CancellationToken ct)
    {
        var sessionResult = await repository.GetByIdAsync(sessionId, ct);
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
            var llmResult = await agentFactory.InvokeAsync(prompt, ct);
            if (llmResult.IsSuccess)
            {
                session.AddMessage(MessageRole.Assistant, llmResult.Value.Response);
            }
        }

        await repository.UpdateAsync(session, ct);

        LogPhaseAdvanced(logger, sessionId, session.Phase);
        return Result.Success(session);
    }

    /// <inheritdoc />
    public async Task<Result> AbandonSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var sessionResult = await repository.GetByIdAsync(sessionId, ct);
        if (sessionResult.IsFailure)
            return Result.Failure(sessionResult.Error);

        var session = sessionResult.Value;
        var abandonResult = session.Abandon();
        if (abandonResult.IsFailure)
            return abandonResult;

        await repository.UpdateAsync(session, ct);

        LogSessionAbandoned(logger, sessionId);
        return Result.Success();
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
}
