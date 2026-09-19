using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     Where a schedule's definition came from: a config file entry the reconciler owns, or an agent-authored
///     schedule created through a tool call.
/// </summary>
public enum ScheduleOrigin
{
    /// <summary>Defined in configuration and reconciled from it on every startup.</summary>
    Config,

    /// <summary>Created by an agent at runtime (e.g. via a tool call), not backed by a config entry.</summary>
    Agent,
}

/// <summary>
///     One configured recurring autonomous agent run: its cron expression, its delivery target (which channel
///     conversation receives the output), the identity it runs as, and when it next fires. Domain stays
///     framework-free: <see cref="Cron"/> is stored as text and is never parsed here — cron parsing and next-occurrence
///     computation belong to the reconciler, which uses Cronos. A later <c>ScheduledRunExecution</c> entity records
///     one row per firing; this aggregate tracks only the schedule itself.
/// </summary>
public sealed class ScheduledRun : Entity<Guid>
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Maximum length of <see cref="Cron"/>.</summary>
    public const int MaxCronLength = 128;

    /// <summary>Maximum length of <see cref="Trigger"/>.</summary>
    public const int MaxTriggerLength = 128;

    /// <summary>Maximum length of <see cref="ChannelId"/>, reusing <see cref="ChannelConversation.MaxChannelIdLength"/>.</summary>
    public const int MaxChannelIdLength = ChannelConversation.MaxChannelIdLength;

    /// <summary>Maximum length of <see cref="ConversationId"/>.</summary>
    public const int MaxConversationIdLength = 128;

    /// <summary>Maximum length of <see cref="PrincipalId"/>.</summary>
    public const int MaxPrincipalIdLength = 128;

    /// <summary>Maximum length of <see cref="Repository"/>.</summary>
    public const int MaxRepositoryLength = 256;

    /// <summary>Gets the unique, human-assigned name of this schedule (e.g. "daily-digest").</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Gets the cron expression, stored verbatim and unparsed. Parsing happens in the reconciler (Cronos).</summary>
    public string Cron { get; private set; } = string.Empty;

    /// <summary>Gets the identifier of what to run when the schedule fires (e.g. a saga name).</summary>
    public string Trigger { get; private set; } = string.Empty;

    /// <summary>Gets which channel adapter receives the run's output (e.g. "telegram", "console").</summary>
    public string ChannelId { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the channel-specific conversation identifier that receives the run's output. Unlike
    ///     <see cref="ChannelConversation.ConversationId"/>, this is never blank: a schedule with no delivery target
    ///     would produce a digest with nowhere to send it.
    /// </summary>
    public string ConversationId { get; private set; } = string.Empty;

    /// <summary>Gets the identity this run executes as (e.g. "schedule:daily-digest").</summary>
    public string PrincipalId { get; private set; } = string.Empty;

    /// <summary>Gets the roles granted to the run's principal. Never empty: a principal with no roles can do nothing.</summary>
    public IReadOnlyList<string> Roles { get; private set; } = [];

    /// <summary>
    ///     Gets the GitHub repository (<c>owner/name</c>) this schedule's workflow sweeps, or <see langword="null"/>
    ///     for a workflow that does not need one. Required whenever <see cref="Trigger"/> is <c>RepoDigest</c> — the
    ///     scout's tools take an explicit <c>owner/name</c> and cannot guess one. That requirement is enforced by
    ///     <c>RepoDigestRepositoryValidator</c> at boot, not by this aggregate, the same way <see cref="Trigger"/>'s
    ///     own membership in <c>ScheduleReconciler.KnownTriggers</c> is enforced by the reconciler rather than here.
    /// </summary>
    public string? Repository { get; private set; }

    /// <summary>Gets where this schedule's definition came from.</summary>
    public ScheduleOrigin Origin { get; private set; }

    /// <summary>Gets when this schedule will next fire (UTC).</summary>
    public DateTime NextRunAt { get; private set; }

    /// <summary>Gets when this schedule last fired (UTC), or null if it has never fired.</summary>
    public DateTime? LastRunAt { get; private set; }

    /// <summary>Gets whether this schedule is active. A disabled schedule is never reconciled or fired.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Gets the cumulative count of occurrences the reconciler determined were missed (e.g. after downtime).</summary>
    public int MissedOccurrences { get; private set; }

    private ScheduledRun() { } // EF Core

    /// <summary>Creates a new, enabled schedule with its first computed occurrence.</summary>
    /// <param name="name">The unique, human-assigned name of this schedule.</param>
    /// <param name="cron">The cron expression, stored verbatim and unparsed.</param>
    /// <param name="trigger">The identifier of what to run when the schedule fires.</param>
    /// <param name="channelId">Which channel adapter receives the run's output.</param>
    /// <param name="conversationId">The channel-specific conversation identifier that receives the run's output.</param>
    /// <param name="principalId">The identity this run executes as.</param>
    /// <param name="roles">The roles granted to the run's principal. Must be non-empty.</param>
    /// <param name="origin">Where this schedule's definition came from.</param>
    /// <param name="nextRunAtUtc">The first computed occurrence (UTC), supplied by the caller.</param>
    /// <param name="repository">
    ///     The GitHub repository (<c>owner/name</c>) this schedule's workflow sweeps, or <see langword="null"/> for
    ///     a workflow that does not need one. Optional and defaulted so every existing caller that predates this
    ///     field keeps compiling; required in practice only when <paramref name="trigger"/> is <c>RepoDigest</c>,
    ///     which <c>RepoDigestRepositoryValidator</c> enforces at boot.
    /// </param>
    /// <returns>A Result containing the new schedule or the first validation error.</returns>
    public static Result<ScheduledRun> Create(
        string name,
        string cron,
        string trigger,
        string channelId,
        string conversationId,
        string principalId,
        IReadOnlyList<string> roles,
        ScheduleOrigin origin,
        DateTime nextRunAtUtc,
        string? repository = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<ScheduledRun>("Name is required.");

        if (name.Length > MaxNameLength)
            return Result.Failure<ScheduledRun>($"Name must be at most {MaxNameLength} characters.");

        var fieldsValidation = ValidateConfigFields(cron, trigger, channelId, conversationId, principalId, roles, repository);
        if (fieldsValidation.IsFailure)
            return Result.Failure<ScheduledRun>(fieldsValidation.Error);

        return Result.Success(new ScheduledRun
        {
            Id = Guid.NewGuid(),
            Name = name,
            Cron = cron,
            Trigger = trigger,
            ChannelId = channelId,
            ConversationId = conversationId,
            PrincipalId = principalId,
            Roles = roles,
            Repository = repository,
            Origin = origin,
            NextRunAt = nextRunAtUtc,
            LastRunAt = null,
            Enabled = true,
            MissedOccurrences = 0,
        });
    }

    /// <summary>Records that this schedule fired, advancing it to its next occurrence.</summary>
    /// <param name="nextRunAtUtc">The next computed occurrence (UTC).</param>
    /// <param name="lastRunAtUtc">The timestamp of the run that just happened (UTC).</param>
    /// <param name="missed">Occurrences the reconciler determined were missed since the previous check, added to the running total.</param>
    public void AdvanceTo(DateTime nextRunAtUtc, DateTime lastRunAtUtc, int missed)
    {
        NextRunAt = nextRunAtUtc;
        LastRunAt = lastRunAtUtc;
        MissedOccurrences += missed;
    }

    /// <summary>Disables this schedule. A disabled schedule is never reconciled or fired.</summary>
    public void Disable()
    {
        Enabled = false;
    }

    /// <summary>Enables this schedule. A re-enabled schedule is reconciled and fired again.</summary>
    public void Enable()
    {
        Enabled = true;
    }

    /// <summary>
    ///     Updates the mutable, config-owned fields of this schedule from a re-read configuration entry, enforcing
    ///     the same non-blank, length-cap, and non-empty-roles invariants as <see cref="Create"/> on those same six
    ///     fields, so this aggregate can never be updated into a state its own constructor would have refused.
    /// </summary>
    /// <param name="cron">The cron expression, stored verbatim and unparsed.</param>
    /// <param name="trigger">The identifier of what to run when the schedule fires.</param>
    /// <param name="channelId">Which channel adapter receives the run's output.</param>
    /// <param name="conversationId">The channel-specific conversation identifier that receives the run's output.</param>
    /// <param name="principalId">The identity this run executes as.</param>
    /// <param name="roles">The roles granted to the run's principal. Must be non-empty.</param>
    /// <param name="repository">
    ///     The GitHub repository (<c>owner/name</c>) this schedule's workflow sweeps, or <see langword="null"/> for
    ///     a workflow that does not need one. See <see cref="Create"/>'s matching parameter for why it is optional.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     A field fails the same validation <see cref="Create"/> enforces. The message names this schedule
    ///     (<see cref="Name"/>) and the field that failed.
    /// </exception>
    public void UpdateFromConfig(
        string cron,
        string trigger,
        string channelId,
        string conversationId,
        string principalId,
        IReadOnlyList<string> roles,
        string? repository = null)
    {
        var validation = ValidateConfigFields(cron, trigger, channelId, conversationId, principalId, roles, repository);
        if (validation.IsFailure)
            throw new InvalidOperationException($"Schedule '{Name}' has an invalid config update: {validation.Error}");

        Cron = cron;
        Trigger = trigger;
        ChannelId = channelId;
        ConversationId = conversationId;
        PrincipalId = principalId;
        Roles = roles;
        Repository = repository;
    }

    /// <summary>
    ///     Validates the config-owned fields shared by <see cref="Create"/> and <see cref="UpdateFromConfig"/>:
    ///     non-blank and length-capped for <paramref name="cron"/>, <paramref name="trigger"/>,
    ///     <paramref name="channelId"/>, <paramref name="conversationId"/>, and <paramref name="principalId"/>;
    ///     non-empty for <paramref name="roles"/>; and, when present, length-capped for
    ///     <paramref name="repository"/>. Excludes <see cref="Name"/>, which only <see cref="Create"/> sets.
    ///     <paramref name="repository"/> has no non-blank requirement here — whether it is required depends on
    ///     <paramref name="trigger"/>, and that workflow-specific rule is <c>RepoDigestRepositoryValidator</c>'s job,
    ///     not this framework-free aggregate's.
    /// </summary>
    private static Result ValidateConfigFields(
        string cron,
        string trigger,
        string channelId,
        string conversationId,
        string principalId,
        IReadOnlyList<string> roles,
        string? repository = null)
    {
        if (string.IsNullOrWhiteSpace(cron))
            return Result.Failure("Cron is required.");

        if (cron.Length > MaxCronLength)
            return Result.Failure($"Cron must be at most {MaxCronLength} characters.");

        if (string.IsNullOrWhiteSpace(trigger))
            return Result.Failure("Trigger is required.");

        if (trigger.Length > MaxTriggerLength)
            return Result.Failure($"Trigger must be at most {MaxTriggerLength} characters.");

        if (string.IsNullOrWhiteSpace(channelId))
            return Result.Failure("Channel id is required.");

        if (channelId.Length > MaxChannelIdLength)
            return Result.Failure($"Channel id must be at most {MaxChannelIdLength} characters.");

        // Unlike ChannelConversation, a blank ConversationId is rejected here: a schedule with no delivery target
        // cannot deliver, so the empty-string case that exists only for the console channel's live binding does
        // not apply to a detached scheduled run.
        if (string.IsNullOrWhiteSpace(conversationId))
            return Result.Failure("Conversation id is required.");

        if (conversationId.Length > MaxConversationIdLength)
            return Result.Failure($"Conversation id must be at most {MaxConversationIdLength} characters.");

        if (string.IsNullOrWhiteSpace(principalId))
            return Result.Failure("Principal id is required.");

        if (principalId.Length > MaxPrincipalIdLength)
            return Result.Failure($"Principal id must be at most {MaxPrincipalIdLength} characters.");

        if (roles is null || roles.Count == 0)
            return Result.Failure("At least one role is required.");

        if (repository is not null && repository.Length > MaxRepositoryLength)
            return Result.Failure($"Repository must be at most {MaxRepositoryLength} characters.");

        return Result.Success();
    }
}
