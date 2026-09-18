using Cronos;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     One entry of the <c>ScheduledRuns</c> configuration array, bound via <c>IConfiguration</c> binding.
///     Cron parsing and trigger validation happen in <see cref="ScheduleReconciler"/>, never here: this type is a
///     plain data holder, deliberately framework- and validation-free so it stays a trivial POCO to bind.
/// </summary>
public sealed class ScheduledRunOptions
{
    /// <summary>The configuration section this type's containing array binds from (<c>ScheduledRuns</c>).</summary>
    public const string SectionName = "ScheduledRuns";

    /// <summary>The unique, human-assigned name of this schedule (e.g. "daily-digest").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The cron expression, stored verbatim; parsed by <see cref="ScheduleReconciler"/> using Cronos.</summary>
    public string Cron { get; set; } = string.Empty;

    /// <summary>The workflow to run when this schedule fires. Must be one of <see cref="ScheduleReconciler.KnownTriggers"/>.</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>Which channel adapter receives the run's output (e.g. "telegram", "console").</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>The channel-specific conversation identifier that receives the run's output.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Whether this schedule should be enabled. Defaults to <see langword="true"/> when omitted.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
///     Reconciles the <c>ScheduledRuns</c> configuration array into the <c>ScheduledRuns</c> table on every host
///     start, so a fresh clone or a config edit becomes the running set of schedules without an operator hand-editing
///     rows. Upserts by <see cref="ScheduledRun.Name"/> for <see cref="ScheduleOrigin.Config"/> rows only; a config
///     row missing from the current configuration is disabled, never deleted, so its history and
///     <c>MissedOccurrences</c> survive a schedule being temporarily removed. Rows with <see cref="ScheduleOrigin.Agent"/>
///     — created by an agent tool call, not by configuration — are never read, written, or disabled here: they are
///     simply excluded from every query this type issues.
/// </summary>
/// <remarks>
///     <para>
///     <b>Validates everything before writing anything.</b> Spec §7 requires the boot to fail rather than defer a
///     configuration error to the schedule's first firing at 07:00. Every entry's cron is parsed with Cronos and
///     every entry's <see cref="ScheduledRunOptions.Trigger"/> is checked against <see cref="KnownTriggers"/> in a
///     dedicated pass before any <see cref="ApplicationDbContext.ScheduledRuns"/> row is added or mutated; only
///     after every entry passes does the method touch the change tracker, and <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
///     runs exactly once, at the very end. A validation failure therefore leaves the database untouched, never
///     partially reconciled.
///     </para>
///     <para>
///     <b>Cron parsing lives here, not in the domain.</b> <see cref="ScheduledRun.Cron"/> is stored as text and
///     never parsed by the aggregate itself — see its class remarks — precisely so that Cronos, a framework
///     concern, stays out of <c>Daedalus.Domain</c>. This reconciler is one of the two places (the other being
///     <see cref="ScheduledRunStore"/>) allowed to call <see cref="CronExpression.Parse(string)"/>.
///     </para>
///     <para>
///     <b>No saga registry exists in this phase.</b> <c>ZeroAlloc.Saga</c> was found undriveable and dropped, so
///     nothing registers saga names to validate a trigger against. Instead, <see cref="KnownTriggers"/> is a small,
///     explicit set of known workflow identifiers a schedule's <see cref="ScheduledRunOptions.Trigger"/> must name
///     one of. Add a second workflow by adding its name to that set.
///     </para>
///     <para>
///     <b>Scoped, like <see cref="ScheduledRunStore"/>, not a singleton over <c>IDbContextFactory</c>.</b> This
///     type injects <see cref="ApplicationDbContext"/> directly. Unlike the store, it needs no shared-connection
///     trick with an outbox writer — reconciliation writes no outbox messages — but the same reasoning that makes
///     "singleton service, scoped context via a factory" the wrong default elsewhere does not automatically make it
///     right here either, and a plain scoped registration is simpler to reason about and to test. Because
///     <see cref="IHostedService"/> instances are singletons, this type is not itself the hosted service:
///     <see cref="ScheduleReconcilerHostedService"/> opens one scope per host start, resolves this type from it, and
///     lets the scope (and its <see cref="ApplicationDbContext"/>) go away once <see cref="ReconcileAsync"/> returns
///     or throws.
///     </para>
/// </remarks>
public sealed partial class ScheduleReconciler(
    ApplicationDbContext db,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<ScheduleReconciler> logger)
{
    /// <summary>
    ///     The configuration key holding the shared identity every configured schedule runs as
    ///     (<c>DetachedRuns:PrincipalId</c> and <c>DetachedRuns:Roles</c>). <c>DetachedRuns</c> also carries a token
    ///     and deadline budget consumed by a later task's options class; this reconciler reads only the identity
    ///     fields it needs and does not bind or own the rest of that section.
    /// </summary>
    public const string DetachedRunsSectionName = "DetachedRuns";

    /// <summary>
    ///     The complete set of workflow identifiers a schedule's <c>Trigger</c> may name. There are no sagas in
    ///     this phase (<c>ZeroAlloc.Saga</c> was dropped as undriveable) — a trigger is validated against this
    ///     explicit set instead of a saga registry. Add a second workflow's name here when one exists.
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownTriggers = ["RepoDigest"];

    private readonly ApplicationDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger<ScheduleReconciler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    ///     Reconciles the <c>ScheduledRuns</c> configuration array into the database: inserts config rows that are
    ///     new, updates in place those that already exist (matched by <see cref="ScheduledRun.Name"/>), and disables
    ///     — never deletes — <see cref="ScheduleOrigin.Config"/> rows whose name no longer appears in configuration.
    ///     <see cref="ScheduleOrigin.Agent"/> rows are excluded from every query here and are therefore never read,
    ///     written, or disabled.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    ///     A <c>ScheduledRuns</c> entry has no <c>Name</c>, two entries share a <c>Name</c>, an entry's <c>Cron</c>
    ///     fails to parse or has no future occurrence, an entry's <c>Trigger</c> is not one of
    ///     <see cref="KnownTriggers"/>, or an entry otherwise fails the same validation
    ///     <see cref="ScheduledRun.Create"/>/<see cref="ScheduledRun.UpdateFromConfig"/> enforce (e.g. a blank
    ///     <c>DetachedRuns:PrincipalId</c>). Every message names the offending schedule. Thrown before any row is
    ///     added or mutated, so the database is untouched when this method throws.
    /// </exception>
    public async ValueTask ReconcileAsync(CancellationToken ct)
    {
        var entries = _configuration.GetSection(ScheduledRunOptions.SectionName).Get<List<ScheduledRunOptions>>()
            ?? [];

        var now = _time.GetUtcNow().UtcDateTime;
        var validated = ValidateAndComputeNextRun(entries, now);

        var principalId = _configuration[$"{DetachedRunsSectionName}:PrincipalId"] ?? string.Empty;
        var roles = _configuration.GetSection($"{DetachedRunsSectionName}:Roles").Get<string[]>() ?? [];

        var existingConfigRuns = await _db.ScheduledRuns
            .Where(r => r.Origin == ScheduleOrigin.Config)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byName = existingConfigRuns.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var configuredNames = new HashSet<string>(entries.Select(e => e.Name), StringComparer.Ordinal);

        foreach (var (entry, nextRunAtUtc) in validated)
        {
            ScheduledRun run;
            if (byName.TryGetValue(entry.Name, out var existing))
            {
                existing.UpdateFromConfig(entry.Cron, entry.Trigger, entry.ChannelId, entry.ConversationId, principalId, roles);
                run = existing;
            }
            else
            {
                var created = ScheduledRun.Create(
                    entry.Name, entry.Cron, entry.Trigger, entry.ChannelId, entry.ConversationId,
                    principalId, roles, ScheduleOrigin.Config, nextRunAtUtc);

                if (created.IsFailure)
                {
                    throw new InvalidOperationException($"Schedule '{entry.Name}' is invalid: {created.Error}");
                }

                run = created.Value;
                _db.ScheduledRuns.Add(run);
            }

            // Origin.Config rows are fully config-owned: this method already overwrites cron, trigger and
            // delivery target from configuration without asking, so Enabled follows the same rule. A row
            // someone (or a previous reconcile) disabled directly in the database is re-enabled here the
            // moment its entry says Enabled: true -- that is the intended escape hatch, not collateral
            // damage. Without this, the first thing an operator does after verifying a channel (flipping
            // Enabled: false to true) would be silently ignored, and the only way to actually turn a
            // schedule on would be a manual database edit.
            if (entry.Enabled)
            {
                run.Enable();
            }
            else
            {
                run.Disable();
            }
        }

        var disabled = 0;
        foreach (var run in existingConfigRuns)
        {
            if (run.Enabled && !configuredNames.Contains(run.Name))
            {
                run.Disable();
                disabled++;
            }
        }

        if (disabled > 0)
        {
            LogDisabledMissing(_logger, disabled);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        LogReconciled(_logger, entries.Count);
    }

    /// <summary>
    ///     Validates every entry — non-blank and unique <c>Name</c>, a parseable <c>Cron</c> with a future
    ///     occurrence, and a <c>Trigger</c> in <see cref="KnownTriggers"/> — before any database write, and returns
    ///     each entry paired with its computed next occurrence (used only when the entry turns out to be new).
    /// </summary>
    private static List<(ScheduledRunOptions Entry, DateTime NextRunAtUtc)> ValidateAndComputeNextRun(
        List<ScheduledRunOptions> entries, DateTime now)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(ScheduledRunOptions, DateTime)>(entries.Count);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                throw new InvalidOperationException($"{ScheduledRunOptions.SectionName} contains an entry with no Name.");
            }

            if (!seenNames.Add(entry.Name))
            {
                throw new InvalidOperationException(
                    $"{ScheduledRunOptions.SectionName} configures more than one schedule named '{entry.Name}'.");
            }

            CronExpression expression;
            try
            {
                expression = CronExpression.Parse(entry.Cron);
            }
            catch (CronFormatException ex)
            {
                throw new InvalidOperationException(
                    $"Schedule '{entry.Name}' has an unparseable cron expression '{entry.Cron}': {ex.Message}", ex);
            }

            if (!KnownTriggers.Contains(entry.Trigger, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Schedule '{entry.Name}' has an unknown Trigger '{entry.Trigger}'. " +
                    $"Known triggers: {string.Join(", ", KnownTriggers)}.");
            }

            var next = expression.GetNextOccurrence(now, TimeZoneInfo.Utc)
                ?? throw new InvalidOperationException(
                    $"Schedule '{entry.Name}' cron '{entry.Cron}' has no future occurrence.");

            result.Add((entry, next));
        }

        return result;
    }

    [LoggerMessage(EventId = 443, Level = LogLevel.Information,
        Message = "Reconciled {Count} configured schedule(s) into the ScheduledRuns table.")]
    private static partial void LogReconciled(ILogger logger, int count);

    [LoggerMessage(EventId = 444, Level = LogLevel.Information,
        Message = "Disabled {Count} config-origin schedule(s) no longer present in configuration.")]
    private static partial void LogDisabledMissing(ILogger logger, int count);
}

/// <summary>
///     Runs <see cref="ScheduleReconciler.ReconcileAsync"/> once at host start, ahead of any hosted service that
///     later sweeps <c>ScheduledRuns</c> for due occurrences, and then validates <c>Thalos:Channels:DefaultAgent</c>
///     and every <see cref="RepoDigestPrompts.AgentNames"/> entry against the agent catalogue in one pass via
///     <see cref="AgentNameValidator"/>. <see cref="ScheduleReconciler"/> is scoped (see its remarks) while hosted
///     services are singletons, so this type's only job is to open one <see cref="IServiceScope"/> per host start,
///     resolve the reconciler from it, and let the scope go away once both checks finish. A failure in either step
///     throws out of <see cref="StartAsync"/> uncaught — by design: an invalid configured schedule, or an agent
///     name nothing in the catalogue answers to, must stop the host at boot rather than defer the failure to the
///     schedule's first firing (or the first channel message routed through the unresolved default agent).
/// </summary>
/// <remarks>
///     The agent-name check lives here — alongside schedule reconciliation, in the one hosted service both share —
///     rather than in a second hosted service, so a slow-booting host runs one boot-validation pass, not two.
///     <c>DefaultAgent</c> is read directly off <see cref="IConfiguration"/> rather than
///     <c>IOptions&lt;ChannelOptions&gt;</c> so this check does not depend on a host having called
///     <c>AddDaedalusChannels</c> — <see cref="ScheduleReconcilerHostedService"/> is registered by
///     <c>AddDaedalusAgents</c> alone, and a host that never configures channels (or leaves
///     <c>Thalos:Channels:DefaultAgent</c> unset) simply drops it from the checked names.
///     <see cref="RepoDigestPrompts.AgentNames"/> is always checked, independent of <c>DefaultAgent</c>: the
///     <c>RepoDigest</c> workflow's scout and writer agents must exist for any host that runs
///     <see cref="RunScoutStep"/> and <see cref="RunWriterStep"/>, whether or not that host also routes
///     channel messages.
/// </remarks>
public sealed class ScheduleReconcilerHostedService(IServiceScopeFactory scopeFactory) : IHostedService
{
    /// <summary>The configuration key <c>ChannelPump</c> resolves against <see cref="IAgentCatalog.Agents"/> to pick an implicit session's agent.</summary>
    public const string DefaultAgentConfigurationKey = "Thalos:Channels:DefaultAgent";

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reconciler = scope.ServiceProvider.GetRequiredService<ScheduleReconciler>();
        await reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);

        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var defaultAgent = configuration[DefaultAgentConfigurationKey];
        var names = string.IsNullOrWhiteSpace(defaultAgent)
            ? RepoDigestPrompts.AgentNames
            : (IReadOnlyList<string>) [defaultAgent, .. RepoDigestPrompts.AgentNames];

        var catalog = scope.ServiceProvider.GetRequiredService<IAgentCatalog>();
        AgentNameValidator.Validate(catalog, names);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
