using ZeroAlloc.Results;

namespace Daedalus.Domain.Entities;

/// <summary>
///     Represents an interactive brainstorming session that guides a user
///     through structured phases to produce a design document, implementation
///     plan, and phased tasks to be worked.
/// </summary>
public sealed class BrainstormSession : AggregateRoot<Guid>
{
    private const string PhaseCompleteMarker = "[PHASE_COMPLETE]";
    private readonly List<BrainstormMessage> _messages = [];

    /// <summary>Gets the parent project ID.</summary>
    public Guid ProjectId { get; private set; }

    /// <summary>Gets the current brainstorm phase.</summary>
    public BrainstormPhase Phase { get; private set; } = BrainstormPhase.ContextGathering;

    /// <summary>Gets the ordered list of messages in this session.</summary>
    public IReadOnlyList<BrainstormMessage> Messages => _messages.AsReadOnly();

    /// <summary>Gets the generated design document (markdown).</summary>
    public string? DesignDocument { get; private set; }

    /// <summary>Gets the generated implementation plan (markdown).</summary>
    public string? ImplementationPlan { get; private set; }

    /// <summary>Gets whether the LLM has signaled phase completion.</summary>
    public bool PhaseCompleteSignaled { get; private set; }

    /// <summary>Gets when this session was created.</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Gets when this session reached a terminal state.</summary>
    public DateTime? CompletedAt { get; private set; }

    /// <summary>
    ///     Gets the row version the session was read with, PostgreSQL's <c>xmin</c>, which guards its save against a
    ///     concurrent write; 0 for a session that was never read.
    /// </summary>
    public uint RowVersion { get; private set; }

    private BrainstormSession() { }

    /// <summary>
    ///     Creates a new brainstorming session for a project.
    /// </summary>
    /// <param name="projectId">The project this session belongs to.</param>
    /// <returns>A Result containing the new session or an error.</returns>
    public static Result<BrainstormSession> Create(Guid projectId)
    {
        if (projectId == Guid.Empty)
            return Result<BrainstormSession>.Failure("Project ID is required.");

        return Result<BrainstormSession>.Success(new BrainstormSession
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Phase = BrainstormPhase.ContextGathering,
            CreatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    ///     Rebuilds a stored session, for persistence only: every value is taken as stored, nothing is validated and no
    ///     domain event is raised.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <param name="projectId">The parent project id.</param>
    /// <param name="phase">The current phase.</param>
    /// <param name="designDocument">The design document, or null.</param>
    /// <param name="implementationPlan">The implementation plan, or null.</param>
    /// <param name="phaseCompleteSignaled">Whether the model signalled the phase complete.</param>
    /// <param name="createdAt">When the session was created.</param>
    /// <param name="completedAt">When the session reached a terminal state, or null.</param>
    /// <param name="rowVersion">The row version the session was read with.</param>
    /// <param name="messages">The session's messages, in conversation order.</param>
    /// <returns>The session as stored.</returns>
    public static BrainstormSession Rehydrate(
        Guid id,
        Guid projectId,
        BrainstormPhase phase,
        string? designDocument,
        string? implementationPlan,
        bool phaseCompleteSignaled,
        DateTime createdAt,
        DateTime? completedAt,
        uint rowVersion,
        IEnumerable<BrainstormMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var session = new BrainstormSession
        {
            Id = id,
            ProjectId = projectId,
            Phase = phase,
            DesignDocument = designDocument,
            ImplementationPlan = implementationPlan,
            PhaseCompleteSignaled = phaseCompleteSignaled,
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            RowVersion = rowVersion
        };
        session._messages.AddRange(messages);
        return session;
    }

    /// <summary>
    ///     Adds a message to the session conversation. If the content contains the
    ///     <c>[PHASE_COMPLETE]</c> marker, the marker is stripped and
    ///     <see cref="PhaseCompleteSignaled"/> is set to <c>true</c>.
    /// </summary>
    /// <param name="role">The role of the message author.</param>
    /// <param name="content">The text content of the message.</param>
    /// <returns>A Result indicating success or failure.</returns>
    public Result AddMessage(MessageRole role, string content)
    {
        if (Phase is BrainstormPhase.Completed or BrainstormPhase.Abandoned)
            return Result.Failure("Cannot add messages to a session in a terminal state.");

        // Detect and strip phase complete marker
        var hasMarker = content.Contains(PhaseCompleteMarker, StringComparison.Ordinal);
        var storedContent = hasMarker
            ? content.Replace(PhaseCompleteMarker, "", StringComparison.Ordinal).TrimEnd()
            : content;

        var messageResult = BrainstormMessage.Create(Id, role, storedContent, Phase);
        if (messageResult.IsFailure)
            return Result.Failure(messageResult.Error);

        _messages.Add(messageResult.Value);

        if (hasMarker)
            PhaseCompleteSignaled = true;

        return Result.Success();
    }

    /// <summary>
    ///     Advances the session to the next phase. Requires that
    ///     <see cref="PhaseCompleteSignaled"/> is <c>true</c>.
    /// </summary>
    /// <returns>A Result indicating success or failure.</returns>
    public Result AdvancePhase()
    {
        if (Phase is BrainstormPhase.Completed or BrainstormPhase.Abandoned)
            return Result.Failure("Cannot advance a session in a terminal state.");

        if (!PhaseCompleteSignaled)
            return Result.Failure("Phase completion has not been signaled by the LLM.");

        Phase = Phase switch
        {
            BrainstormPhase.ContextGathering => BrainstormPhase.Clarification,
            BrainstormPhase.Clarification => BrainstormPhase.Proposals,
            BrainstormPhase.Proposals => BrainstormPhase.DesignReview,
            BrainstormPhase.DesignReview => BrainstormPhase.PlanGeneration,
            BrainstormPhase.PlanGeneration => BrainstormPhase.TaskCreation,
            BrainstormPhase.TaskCreation => BrainstormPhase.Completed,
            _ => throw new InvalidOperationException($"Unexpected phase: {Phase}")
        };

        PhaseCompleteSignaled = false;

        if (Phase == BrainstormPhase.Completed)
            CompletedAt = DateTime.UtcNow;

        return Result.Success();
    }

    /// <summary>
    ///     Abandons the session, setting it to the <see cref="BrainstormPhase.Abandoned"/> terminal state.
    /// </summary>
    /// <returns>A Result indicating success or failure.</returns>
    public Result Abandon()
    {
        if (Phase is BrainstormPhase.Completed or BrainstormPhase.Abandoned)
            return Result.Failure("Cannot abandon a session in a terminal state.");

        Phase = BrainstormPhase.Abandoned;
        CompletedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>
    ///     Sets the design document produced during the brainstorming session.
    /// </summary>
    /// <param name="designDocument">The design document content (markdown).</param>
    /// <returns>A Result indicating success or failure.</returns>
    public Result SetDesignDocument(string designDocument)
    {
        if (string.IsNullOrWhiteSpace(designDocument))
            return Result.Failure("Design document cannot be empty.");

        DesignDocument = designDocument;
        return Result.Success();
    }

    /// <summary>
    ///     Sets the implementation plan produced during the brainstorming session.
    /// </summary>
    /// <param name="plan">The implementation plan content (markdown).</param>
    /// <returns>A Result indicating success or failure.</returns>
    public Result SetImplementationPlan(string plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
            return Result.Failure("Implementation plan cannot be empty.");

        ImplementationPlan = plan;
        return Result.Success();
    }
}
