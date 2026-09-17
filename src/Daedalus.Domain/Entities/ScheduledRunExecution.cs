using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     One firing of a <see cref="ScheduledRun"/>: the row that replaces the in-memory state a saga would have
///     held. It walks <see cref="RunStep.Pending"/> through <see cref="RunStep.Scout"/>,
///     <see cref="RunStep.Writer"/>, and <see cref="RunStep.Deliver"/> to <see cref="RunStep.Done"/>, or to
///     <see cref="RunStep.Failed"/> from any non-terminal step, persisting each stage's output
///     (<see cref="Findings"/>, then <see cref="Digest"/>) as it goes so a crash mid-run never re-pays for work
///     already done. Domain stays framework-free: nothing here reads the clock — every transition takes
///     <c>nowUtc</c> from its caller. The pairing of <see cref="ScheduleId"/> and <see cref="OccurrenceAt"/> is the
///     saga's old correlation key; a later task adds a UNIQUE (ScheduleId, OccurrenceAt) database index over it so
///     the same occurrence can never be enqueued twice.
/// </summary>
public sealed class ScheduledRunExecution : Entity<Guid>
{
    /// <summary>Maximum length of <see cref="ChannelId"/>, reusing <see cref="ChannelConversation.MaxChannelIdLength"/>.</summary>
    public const int MaxChannelIdLength = ChannelConversation.MaxChannelIdLength;

    /// <summary>Maximum length of <see cref="ConversationId"/>.</summary>
    public const int MaxConversationIdLength = 128;

    /// <summary>Maximum length of <see cref="PrincipalId"/>.</summary>
    public const int MaxPrincipalIdLength = 128;

    /// <summary>Gets the schedule that fired, producing this execution.</summary>
    public Guid ScheduleId { get; private set; }

    /// <summary>
    ///     Gets the instant this occurrence was due (UTC). Paired with <see cref="ScheduleId"/> as the idempotency
    ///     key: the same occurrence of the same schedule must never produce two rows.
    /// </summary>
    public DateTime OccurrenceAt { get; private set; }

    /// <summary>Gets which step of the run should happen next.</summary>
    public RunStep Step { get; private set; }

    /// <summary>Gets the scout stage's output, or null until <see cref="RecordFindings"/> has run.</summary>
    public string? Findings { get; private set; }

    /// <summary>Gets the writer stage's output, or null until <see cref="RecordDigest"/> has run.</summary>
    public string? Digest { get; private set; }

    /// <summary>Gets which channel adapter receives this run's output (e.g. "telegram", "console").</summary>
    public string ChannelId { get; private set; } = string.Empty;

    /// <summary>Gets the channel-specific conversation identifier that receives this run's output.</summary>
    public string ConversationId { get; private set; } = string.Empty;

    /// <summary>Gets the identity this run executes as (e.g. "schedule:daily-digest").</summary>
    public string PrincipalId { get; private set; } = string.Empty;

    /// <summary>Gets the roles granted to this run's principal. Never empty: a principal with no roles can do nothing.</summary>
    public IReadOnlyList<string> Roles { get; private set; } = [];

    /// <summary>Gets how many times this execution has failed.</summary>
    public int Attempts { get; private set; }

    /// <summary>Gets the most recent failure's message, or null if this execution has never failed.</summary>
    public string? LastError { get; private set; }

    /// <summary>Gets when this execution was created (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Gets when this execution was last updated (UTC).</summary>
    public DateTime UpdatedAt { get; private set; }

    private ScheduledRunExecution() { } // EF Core

    /// <summary>Creates a new execution at <see cref="RunStep.Pending"/>, with no output and no attempts.</summary>
    /// <param name="scheduleId">The schedule that fired.</param>
    /// <param name="occurrenceAtUtc">The instant this occurrence was due. Must be UTC.</param>
    /// <param name="channelId">Which channel adapter receives this run's output.</param>
    /// <param name="conversationId">The channel-specific conversation identifier that receives this run's output.</param>
    /// <param name="principalId">The identity this run executes as.</param>
    /// <param name="roles">The roles granted to this run's principal. Must be non-empty.</param>
    /// <param name="nowUtc">The creation timestamp (UTC).</param>
    /// <returns>A Result containing the new execution or the first validation error.</returns>
    public static Result<ScheduledRunExecution> Create(
        Guid scheduleId,
        DateTime occurrenceAtUtc,
        string channelId,
        string conversationId,
        string principalId,
        IReadOnlyList<string> roles,
        DateTime nowUtc)
    {
        if (scheduleId == Guid.Empty)
            return Result.Failure<ScheduledRunExecution>("Schedule id is required.");

        if (occurrenceAtUtc.Kind != DateTimeKind.Utc)
            return Result.Failure<ScheduledRunExecution>("Occurrence must be UTC.");

        if (string.IsNullOrWhiteSpace(channelId))
            return Result.Failure<ScheduledRunExecution>("Channel id is required.");

        if (channelId.Length > MaxChannelIdLength)
            return Result.Failure<ScheduledRunExecution>($"Channel id must be at most {MaxChannelIdLength} characters.");

        if (string.IsNullOrWhiteSpace(conversationId))
            return Result.Failure<ScheduledRunExecution>("Conversation id is required.");

        if (conversationId.Length > MaxConversationIdLength)
            return Result.Failure<ScheduledRunExecution>($"Conversation id must be at most {MaxConversationIdLength} characters.");

        if (string.IsNullOrWhiteSpace(principalId))
            return Result.Failure<ScheduledRunExecution>("Principal id is required.");

        if (principalId.Length > MaxPrincipalIdLength)
            return Result.Failure<ScheduledRunExecution>($"Principal id must be at most {MaxPrincipalIdLength} characters.");

        if (roles is null || roles.Count == 0)
            return Result.Failure<ScheduledRunExecution>("At least one role is required.");

        return Result.Success(new ScheduledRunExecution
        {
            Id = Guid.CreateVersion7(),
            ScheduleId = scheduleId,
            OccurrenceAt = occurrenceAtUtc,
            Step = RunStep.Pending,
            ChannelId = channelId,
            ConversationId = conversationId,
            PrincipalId = principalId,
            Roles = roles,
            Attempts = 0,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        });
    }

    /// <summary>Advances a pending execution to the scout step.</summary>
    /// <param name="nowUtc">The transition timestamp (UTC).</param>
    /// <exception cref="InvalidOperationException">This execution is not at <see cref="RunStep.Pending"/>.</exception>
    public void BeginScout(DateTime nowUtc)
    {
        RequireStep(RunStep.Pending, nameof(BeginScout));
        Step = RunStep.Scout;
        UpdatedAt = nowUtc;
    }

    /// <summary>Persists the scout stage's output and advances to the writer step.</summary>
    /// <param name="findings">The scout stage's output.</param>
    /// <param name="nowUtc">The transition timestamp (UTC).</param>
    /// <exception cref="InvalidOperationException">This execution is not at <see cref="RunStep.Scout"/>.</exception>
    public void RecordFindings(string findings, DateTime nowUtc)
    {
        RequireStep(RunStep.Scout, nameof(RecordFindings));
        Findings = findings;
        Step = RunStep.Writer;
        UpdatedAt = nowUtc;
    }

    /// <summary>Persists the writer stage's output and advances to the deliver step.</summary>
    /// <param name="digest">The writer stage's output.</param>
    /// <param name="nowUtc">The transition timestamp (UTC).</param>
    /// <exception cref="InvalidOperationException">This execution is not at <see cref="RunStep.Writer"/>.</exception>
    public void RecordDigest(string digest, DateTime nowUtc)
    {
        RequireStep(RunStep.Writer, nameof(RecordDigest));
        Digest = digest;
        Step = RunStep.Deliver;
        UpdatedAt = nowUtc;
    }

    /// <summary>Marks a delivered execution as done. Terminal.</summary>
    /// <param name="nowUtc">The transition timestamp (UTC).</param>
    /// <exception cref="InvalidOperationException">This execution is not at <see cref="RunStep.Deliver"/>.</exception>
    public void Complete(DateTime nowUtc)
    {
        RequireStep(RunStep.Deliver, nameof(Complete));
        Step = RunStep.Done;
        UpdatedAt = nowUtc;
    }

    /// <summary>
    ///     Records a failure from any non-terminal step, moving this execution to <see cref="RunStep.Failed"/> and
    ///     incrementing <see cref="Attempts"/>. Unlike the other transitions, this has no single required starting
    ///     step: a step can fail regardless of which non-terminal step it failed at.
    /// </summary>
    /// <param name="error">The failure message. Overwrites any previous <see cref="LastError"/>.</param>
    /// <param name="nowUtc">The transition timestamp (UTC).</param>
    public void Fail(string error, DateTime nowUtc)
    {
        Step = RunStep.Failed;
        LastError = error;
        Attempts += 1;
        UpdatedAt = nowUtc;
    }

    /// <summary>
    ///     Guards a transition that requires this execution to be at a specific step. Throwing here is a
    ///     programming-error signal, not a redelivery path: a store's step check is expected to catch redelivery
    ///     before it ever reaches this entity, so reaching this throw means that check is missing.
    /// </summary>
    /// <param name="expected">The step the caller requires.</param>
    /// <param name="operation">The name of the operation being guarded, for the exception message.</param>
    /// <exception cref="InvalidOperationException">This execution is not at <paramref name="expected"/>.</exception>
    private void RequireStep(RunStep expected, string operation)
    {
        if (Step != expected)
        {
            throw new InvalidOperationException(
                $"{operation} requires step {expected}, but this execution is at {Step}. " +
                "The dispatcher is expected to have checked the step before calling; reaching here means that check is missing.");
        }
    }
}
