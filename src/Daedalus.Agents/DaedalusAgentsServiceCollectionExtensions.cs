using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using AI.Sentinel;
using AI.Sentinel.Detection;
using Daedalus.Agents.Channels;
using Daedalus.Agents.Charters;
using Daedalus.Agents.Git;
using Daedalus.Agents.Memory;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Security;
using Daedalus.Agents.Sessions;
using Daedalus.Agents.Skills;
using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Agents.Tools;
using Daedalus.Infrastructure.Extensions;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Thalos;
using Thalos.Anthropic;
using Thalos.Caching;
using Thalos.Git;
using Thalos.Git.LibGit2Sharp;
using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Memory;
using Thalos.Memory.RagNet;
using Thalos.Sandbox;
using Thalos.Sandbox.Docker;
using Thalos.Sentinel;
using Thalos.Skills;
using Thalos.Skills.Charters;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using Thalos.Workspaces;

namespace Daedalus.Agents;

/// <summary>Composition root for the Thalos-based agent stack. Ralph Loop registrations are untouched (strangler).</summary>
public static partial class DaedalusAgentsServiceCollectionExtensions
{
    /// <summary>The Thalos tool-source name of <see cref="DaedalusKnowledgeTools"/>; tools appear as <c>daedalus__{tool}</c>.</summary>
    public const string KnowledgeToolSourceName = "daedalus";

    /// <summary>
    ///     The Thalos tool-source name of <see cref="DaedalusRepoActionTools"/>; tools appear as
    ///     <c>repoaction__{tool}</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A separate source, and this name in particular.</b> The unattended scout's allow-list is
    ///         <c>daedalus__*</c>. Naming this source <c>daedalus_write</c> would produce
    ///         <c>daedalus_write__comment_on_issue</c>, which is one character away from being swallowed by that
    ///         glob by accident; <c>repoaction__*</c> cannot be. The read tools stay in
    ///         <see cref="KnowledgeToolSourceName"/> for the mirror-image reason: the scout already allows that
    ///         prefix, so reads reach it with no configuration change at all.
    ///     </para>
    ///     <para>
    ///         The source split and the agent allow-list are defence in depth. The boundary that actually holds is
    ///         the <c>repoaction__*</c> → <see cref="DeveloperPolicy"/> binding in <c>Thalos:ToolPolicies</c>: a
    ///         scheduled run authenticates as <c>schedule:daedalus</c> with roles <c>["reader"]</c>, so
    ///         <c>DefaultToolAuthorizer</c> denies every tool here whatever an agent's tool list says.
    ///     </para>
    /// </remarks>
    public const string RepoActionToolSourceName = "repoaction";

    /// <summary>
    ///     The Thalos tool-source name of <see cref="GitActionTools"/>; tools appear as <c>git__{tool}</c>
    ///     (<c>git__create_branch</c>, <c>git__commit</c>, <c>git__push</c>, <c>git__open_pull_request</c>).
    /// </summary>
    /// <remarks>
    ///     <b>Same boundary as <see cref="RepoActionToolSourceName"/>, owned by Thalos instead of Daedalus.</b>
    ///     <see cref="GitActionTools"/> is defined in <c>Thalos.NET.Git</c> — Daedalus only registers it and binds
    ///     <c>git__*</c> to <see cref="DeveloperPolicy"/> in <c>Thalos:ToolPolicies</c>, exactly as it does for
    ///     <c>repoaction__*</c>. The property that makes either binding safe is the same one: a scheduled run
    ///     authenticates as <c>schedule:daedalus</c> with roles <c>["reader"]</c>, so <c>DefaultToolAuthorizer</c>
    ///     denies every <c>git__*</c> tool whatever an agent's tool list contains.
    /// </remarks>
    public const string GitToolSourceName = "git";

    /// <summary>
    ///     The Thalos tool-source name of <see cref="DaedalusManufactureTools"/>; tools appear as
    ///     <c>manufacture__{tool}</c> (today, only <c>manufacture__start</c>).
    /// </summary>
    /// <remarks>
    ///     Not <c>workflow</c> — that name is deliberately never used by any tool source, so it can never collide
    ///     with a resume- or cancel-capable tool if one were ever added by mistake; see
    ///     <c>ResumeToolBoundaryTests.No_tool_source_is_named_workflow</c>. Same boundary shape as
    ///     <see cref="RepoActionToolSourceName"/> and <see cref="GitToolSourceName"/>: registered unconditionally —
    ///     <see cref="Workflow.IManufactureRunStarter"/> always resolves, engine on or off — and bound to
    ///     <see cref="DeveloperPolicy"/> in <c>Thalos:ToolPolicies</c>, because starting a run is unattended spend
    ///     with no human in the turn.
    /// </remarks>
    public const string ManufactureToolSourceName = "manufacture";

    /// <summary>
    ///     The Thalos tool-source name of <see cref="DaedalusIssueTools"/>; tools appear as <c>issues__get</c> and
    ///     <c>issues__search</c>. Ungated reads. A separate source so that only the agents that name it, the reviewer and
    ///     the chat Architect, carry its definitions; see <see cref="DaedalusIssueTools"/>.
    /// </summary>
    public const string IssuesToolSourceName = "issues";

    /// <summary>Name of the application database connection string (<c>ConnectionStrings:daedalus</c>), shared with the Rag.NET memory index.</summary>
    public const string DatabaseConnectionName = "daedalus";

    /// <summary>
    ///     Registers Thalos (Anthropic provider, <see cref="PostgresAgentSessionStore"/>, <see cref="DaedalusKnowledgeTools"/>,
    ///     MCP servers from <see cref="DaedalusAgentsOptions.McpConfigPath"/>, the <see cref="DeveloperPolicy"/>, configured
    ///     agents/tool policies, memory (<c>IMemoryService</c> over <see cref="PostgresMemoryStore"/> and the Rag.NET index on
    ///     the application database), skills (the catalogue and <c>skills__*</c> tools over <see cref="PostgresSkillStore"/>)
    ///     and — when enabled — AI.Sentinel) from the <c>Thalos</c> section of
    ///     <paramref name="configuration"/>, plus the <see cref="AgentSessionCrashRecovery"/> hosted service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Host configuration; <c>Thalos</c>, <c>Thalos:Anthropic</c>, <c>Thalos:Memory</c>, <c>Thalos:Skills</c> and <c>ConnectionStrings:daedalus</c> are read.</param>
    /// <param name="environment">Used to resolve a relative <see cref="DaedalusAgentsOptions.McpConfigPath"/> and relative <c>Thalos:Skills:Roots</c> against the content root.</param>
    /// <param name="embeddingGenerator">
    ///     Optional embedding generator handed to AI.Sentinel. Without it Sentinel's semantic detectors (prompt injection,
    ///     jailbreak, exfiltration, …) return Clean and only the lexical/operational detectors run — Sentinel logs a warning
    ///     per agent pipeline. <c>AddAISentinel</c> runs its configure delegate at registration time (no service provider),
    ///     which is why the host passes the instance instead of it being resolved from DI. The memory index resolves the
    ///     generator from DI (a passed instance is registered with <c>TryAddSingleton</c>, so a host registration wins);
    ///     without one the index is unavailable — remember stores <c>index_pending</c>, recall adds nothing.
    /// </param>
    /// <remarks>
    ///     Requires <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c> (see <c>AddApplicationDatabase</c>) and the
    ///     Infrastructure services behind the knowledge tools (<c>IFailurePatternDatabase</c>) to be registered by the host.
    ///     Nothing here needs <c>ANTHROPIC_API_KEY</c> at registration time; the provider reads it lazily on first use.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     An agent id is neither a ULID nor a GUID, a Sentinel action or detector name is unknown, a <c>Thalos:Memory</c>
    ///     value is out of range, a <c>Thalos:Skills</c> value is out of range or a configured <c>Thalos:Skills:Roots</c>
    ///     entry does not exist, <c>Thalos:Squad:FallbackAgentName</c> is blank, or memory is already registered on this
    ///     collection (see <see cref="AddDaedalusMemory"/>).
    /// </exception>
    public static IServiceCollection AddDaedalusAgents(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);
        return services.AddDaedalusAgents(options, configuration, environment, embeddingGenerator);
    }

    /// <summary>
    ///     Test seam: same as <see cref="AddDaedalusAgents(IServiceCollection, IConfiguration, IHostEnvironment, IEmbeddingGenerator{string, Embedding{float}}?)"/>,
    ///     but takes an already-bound <see cref="DaedalusAgentsOptions"/> instead of binding <paramref name="configuration"/>
    ///     itself. <paramref name="configuration"/> is still read for the connection string and for the
    ///     sub-builders (<c>UseAnthropic</c>, memory, workflow) that bind their own sections directly. Lets a test
    ///     mutate one field of an otherwise shipped configuration — for example, clearing a chartered agent's
    ///     <c>Tools</c> — without fighting configuration-override syntax for arrays; see
    ///     <c>SquadConfigurationDriftTests</c> and <c>CharteredRoleCompositionTests</c>.
    /// </summary>
    internal static IServiceCollection AddDaedalusAgents(
        this IServiceCollection services,
        DaedalusAgentsOptions options,
        IConfiguration configuration,
        IHostEnvironment environment,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ThrowIfMemoryAlreadyRegistered(services, nameof(AddDaedalusAgents));

        var connectionString = ResolveConnectionString(configuration);

        // The Ralph MCP failure-patterns tool class doubles as the implementation behind DaedalusKnowledgeTools (fresh
        // scope per invocation). search_learnings is not wrapped: agents recall learnings through the memory__* tools.
        services.AddScoped<DaedalusFailurePatternsTools>();

        // Sessions left in Running by a crashed host are reset to Idle before the host serves requests.
        services.AddHostedService<AgentSessionCrashRecovery>();

        // Reconciles the ScheduledRuns configuration array into the ScheduledRuns table before the host serves
        // requests: an invalid configured schedule (bad cron, unknown Trigger) must stop the host at boot rather
        // than defer the failure to that schedule's first firing (spec §7). Registered ahead of any future sweeper
        // hosted service, which reads the table this reconciler populates. ScheduleReconciler is scoped (see its
        // own remarks), so it is registered separately from the singleton ScheduleReconcilerHostedService that
        // resolves it from a fresh scope per host start. That hosted service also validates
        // Thalos:Channels:DefaultAgent against the agent catalogue (AgentNameValidator) in the same boot pass.
        services.AddScoped<ScheduleReconciler>();
        services.AddHostedService<ScheduleReconcilerHostedService>();

        // Durable outbound chat delivery: writer + EF Core store + poller for ChannelMessageQueued; see
        // AddChannelOutbox for the chosen polling/batch/retry values. AddDaedalusChannels Replaces the
        // library default with ChannelMessageQueuedDispatcher. Nothing writes to this outbox in phase 1.4 —
        // it is durability laid down for 1.5 proactive pushes. See the design doc, section 9.
        services.AddChannelOutbox();

        // The deadline every detached agent turn runs under: SubagentRunExecutor applies it to the scheduling
        // steps this outbox dispatches, and BudgetedSubagentRunner to every workflow turn. Read from
        // configuration here rather than from IOptions<DetachedRunOptions> so both lease checks fail at
        // registration, like every other configuration check in this method, instead of at first resolution.
        var turnDeadline = TimeSpan.FromSeconds(
            configuration.GetSection("DetachedRuns").GetValue<int>(nameof(DetachedRunOptions.DeadlineSeconds)));

        AddGitHub(services, configuration);

        // Thalos's pull-request publisher abstraction, implemented over Daedalus's existing pull-request-factory
        // dispatcher, so the future git pull-request tool inherits GitHub and Azure DevOps routing for free. The
        // factory it delegates to is registered by AddCodeAnalysisServices in Daedalus.Infrastructure, not here — a
        // host calling this method alone resolves the publisher fine but only fails, at first use, if it never
        // called that one too. That mirrors how the factory itself already gets consumed across composition roots.
        // The concrete type is registered once, and both interfaces map to that one scoped instance. The lookup is its
        // own interface so GitActionTools, which depends on IPullRequestPublisher only, never gains a read surface
        // through it. Mapped to the concrete registration, not cast back from IPullRequestPublisher, so a host that
        // substitutes the publisher still resolves the lookup.
        services.AddScoped<ThalosPullRequestPublisher>();
        services.AddScoped<IPullRequestPublisher>(sp => sp.GetRequiredService<ThalosPullRequestPublisher>());
        services.AddScoped<IOpenPullRequestLookup>(sp => sp.GetRequiredService<ThalosPullRequestPublisher>());

        ValidateMemoryConfig(options.Memory);
        services.TryAddSingleton(options.Memory);
        services.TryAddSingleton(options.Memory.RalphRecall);
        services.TryAddSingleton<ILearningsMemory, ThalosLearningsMemory>();

        var skillRoots = ResolveSkillRoots(options.Skills, environment);
        ValidateSkillsConfig(options.Skills, skillRoots);
        services.TryAddSingleton(options.Skills);

        var charterRoots = ResolveCharterRoots(options.CharterRoots, environment);
        ValidateCharterConfig(options);

        // The role→agent split for the manufacturing squad. Registered unconditionally (unlike the Workflow
        // block below): SquadAgentResolver has no database or hosted-service dependency, and a host that never
        // dispatches a squad-staffed process node simply never calls Resolve.
        ValidateSquadConfig(options.Squad);
        services.TryAddSingleton(options.Squad);
        services.TryAddSingleton<SquadAgentResolver>();

        ValidateContentConfig(options.Content);

        // Checked whether or not the engine is on: Daedalus.Cli ships the same write grant with the engine off, and a
        // grant that only fails validation once someone flips Enabled is a grant nobody reviewed.
        ValidateWorkflowWriteConfig(options.Workflow, configuration.GetSection(WorkflowConfig.SectionName + ":WriteGrants"));

        // Phase 2.5, task B9: csharp-write lets a granted workflow turn apply Roslyn code actions. That is only safe
        // when the MCP source it is bound to is run-scoped, so the turn is served by its own run's server over its own
        // worktree; bound to a host-scoped source it would change this host's own solution. Checked at boot whenever
        // the engine is on: with it off there is no workflow caller, and csharp-write admits developer and admin only.
        var mcpConfigPath = ResolveMcpConfigPath(options.McpConfigPath, environment);
        if (options.Workflow.Enabled)
        {
            ValidateCSharpWriteBindings(options.ToolPolicies, mcpConfigPath);
        }

        // Phase 2.2 Part B: the workflow engine's own NpgsqlDataSource — used by WorkflowOutboxDispatchService's
        // poller, not by OrmWorkflowStore itself, which opens its own `new NpgsqlConnection(_options.ConnectionString)`
        // per call (Thalos.NET.Workflow.Orm gives it no seam to accept a shared data source instead). Registered
        // here anyway so the poller's connections are visible to the same pooling/health surface as everything
        // else built on Npgsql, deliberately not ApplicationDbContext's connection — the whole point of
        // Thalos.NET.Workflow.Orm is that it is EF-free (EF Core is Milestone 3's AOT blocker). Same physical
        // "daedalus" database as ApplicationDbContext — the AppHost declares only one Postgres resource — reached
        // through a completely separate ADO.NET path. Factory registration (not AddSingleton(instance)) so the
        // host's container disposes it on shutdown. Gated on Workflow.Enabled — see that option's own remarks
        // for why a host whose database has none of these tables must not wire any of this at all.
        var processesRoot = ResolveContentRoot(options.Workflow.ProcessesRoot, environment);
        var dataRoot = ResolveDataRoot(options.Workflow.DataRoot);
        var standingInstructionsPath = "";
        if (options.Workflow.Enabled)
        {
            // Phase 2.5, task B11: relative, inside the run's worktree. ManufactureRunStarter and
            // StandingInstructionsWriter apply the same check when they are built; this makes a bad value fail the
            // boot instead of the first run.
            standingInstructionsPath = StandingInstructionsRelativePath(options.Workflow.StandingInstructionsPath);
            ValidateDataRootForRunServers(dataRoot);
        }

        // Registered unconditionally, the same as options.Memory/options.Skills/options.Squad above: task B5's
        // StandingInstructionsWriter resolves its own path from this instance via DI (see AddDaedalusWorkflow),
        // and only AddDaedalusWorkflow — gated on Enabled below — ever constructs one.
        services.TryAddSingleton(options.Workflow);
        if (options.Workflow.Enabled)
        {
            services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        }

        // Always registered, engine on or off — see IManufactureRunStarter's own remarks. AddDaedalusWorkflow
        // below replaces this with the real ManufactureRunStarter when Workflow.Enabled; every other host keeps
        // this default, so WorkflowRunsController and DaedalusManufactureTools stay constructible either way and
        // report the engine is off instead of failing DI resolution.
        services.TryAddSingleton<IManufactureRunStarter, DisabledManufactureRunStarter>();

        // Phase 2.8: always registered, like the starter above. It compensates for a started run that could not be
        // attached to its task; AddDaedalusWorkflow replaces it next to the starter when Workflow.Enabled.
        services.TryAddSingleton<IWorkflowRunCanceller, DisabledWorkflowRunCanceller>();

        // Phase 2.8, amendment A2: always registered, like the starter above, so task reads resolve on every host.
        // AddDaedalusWorkflow replaces it with the engine reader when Workflow.Enabled.
        services.TryAddSingleton<IWorkflowRunStatusReader, DisabledWorkflowRunStatusReader>();

        // The memory index embeds with the DI generator; a host that only hands the instance to this call (for Sentinel) still gets a working index.
        if (embeddingGenerator is not null)
        {
            services.TryAddSingleton(embeddingGenerator);
        }

        services.AddThalos(thalos =>
        {
            // This host owns the Rag.NET schema: the API is the only host that creates rag_chunks (see AddDaedalusMemory).
            ConfigureMemory(thalos, configuration.GetSection(MemoryConfig.SectionName), options.Memory, connectionString, ensureSchema: true);
            // Skills are API-host only: the Ralph console runs no Thalos agents (see AddDaedalusMemory).
            ConfigureSkills(thalos, configuration.GetSection(SkillsConfig.SectionName), options.Skills, skillRoots);


            if (options.Workflow.Enabled)
            {
                // Phase 2.2 Part B: the workflow store and process-definition store. OrmWorkflowStore opens its
                // own connection per call from this same connection string directly — it does not use the
                // NpgsqlDataSource registered above, which exists for the outbox poller instead (see that
                // registration's remarks). EnsureSchemaOnStartup is deliberately false: migration 1004
                // (process_definition.content_hash, NOT NULL, no default) is not backward compatible with
                // pre-1004 code, so applying it ahead of a rolling deploy would break process syncing with
                // 23502 on every instance not yet replaced. Daedalus.Migrations applies WorkflowOrmMigrations
                // explicitly, as the same pre-boot deploy step it already runs ApplicationDbContext's EF Core
                // migrations as — see that project's Program.cs.
                thalos.AddWorkflowOrm(o =>
                {
                    o.ConnectionString = connectionString;
                    o.EnsureSchemaOnStartup = false;
                });

                // Phase 2.5: the credential source is what lets the provider and the workspace git reach a private
                // remote; without it every clone, fetch and push is anonymous, which a private repository refuses. The
                // sandboxed provider's mirror and publish worktrees use it too.
                thalos.Services.TryAddSingleton<IGitCredentialSource, GitHubGitCredentialSource>();
                if (options.Workflow.Sandbox.Enabled)
                {
                    ConfigureSandboxMode(thalos, options.Workflow, dataRoot, standingInstructionsPath);
                }
                else
                {
                    ConfigureLocalMode(thalos, options.Workflow, dataRoot, standingInstructionsPath);
                }
            }

            thalos.UseAnthropic(configuration)
                .UseSessionStore<PostgresAgentSessionStore>()
                // Phase 2.5, spec decision 9: the provider-neutral cache hints, placed outermost so Sentinel and every
                // other decorator further in sees the hinted request. Anthropic's translator
                // (Thalos:Anthropic:PromptCaching, on by default) turns them into cache_control; a provider without a
                // translator ignores them.
                .UsePromptCaching()
                // Reads join the existing source the scout already allows; writes go in their own, which its
                // daedalus__* glob cannot name. See RepoActionToolSourceName for why the split is not the boundary.
                // DaedalusReviewTools joins this source rather than getting its own: report_review_outcome is a
                // pure validator with no side effect on anything outside its own turn, so it needs no separate
                // write boundary, and the reviewer's daedalus__* grant already names it.
                .AddLocalTools(KnowledgeToolSourceName, typeof(DaedalusKnowledgeTools), typeof(DaedalusScheduleTools), typeof(DaedalusRepoTools), typeof(DaedalusReviewTools))
                .AddLocalTools(RepoActionToolSourceName, typeof(DaedalusRepoActionTools))
                .AddLocalTools(IssuesToolSourceName, typeof(DaedalusIssueTools))
                // Registered unconditionally, like the two sources above: IManufactureRunStarter always resolves
                // (the disabled default when Workflow.Enabled is false), so this source is always present for
                // Every_manufacture_tool_is_bound_to_the_developer_policy to check against real, shipped config
                // rather than passing vacuously on a host that never registers it.
                .AddLocalTools(ManufactureToolSourceName, typeof(DaedalusManufactureTools))
                // Thalos's own local-git write capability (branch, commit, push, open pull request). UseLibGit2SharpGit
                // registers the IGitWriteService implementation GitActionTools needs alongside IPullRequestPublisher
                // (registered above, in AddDaedalusAgents proper). Same write boundary as repoaction__*, just owned by
                // Thalos: see GitToolSourceName for why the git__* -> developer binding is what actually holds it.
                .UseLibGit2SharpGit()
                .AddLocalTools(GitToolSourceName, typeof(GitActionTools))
                // One .mcp.json serves both modes: in sandbox mode the loader makes every run-scoped entry remote, so
                // a run's Roslyn server is the one inside its sandbox. See McpServersLoader.
                .AddMcpServers(McpServersLoader.Load(mcpConfigPath, options.Workflow.Sandbox.Enabled))
                .AddPolicy<DeveloperPolicy>()
                .AddPolicy<WorkspaceWritePolicy>()
                .AddPolicy<CSharpWritePolicy>();

            foreach (var binding in options.ToolPolicies)
            {
                thalos.RequireToolPolicy(binding.Pattern, binding.Policy);
            }

            foreach (var agent in options.Agents)
            {
                if (agent.Chartered)
                {
                    // Registered as an AgentEnvelope below instead: prose, model and skills come from its role
                    // charter, not this entry.
                    continue;
                }

                thalos.AddAgent(ToDefinition(agent));
            }

            // Chartered agents: the tool envelope (identity + Tools + memory) is built here, from configuration,
            // exactly like every other agent - only the prose/model/skills half moves to roles/*.md. Called
            // unconditionally (Envelopes may be empty, as on Daedalus.Cli, which declares no chartered agent) so
            // every host that calls AddDaedalusAgents gets the same CharteredAgentCatalog/CharterSyncService
            // wiring rather than two different IAgentCatalog shapes depending on whether any role is chartered.
            thalos.UseRoleCharters(o =>
                {
                    o.Roots = [.. charterRoots];
                    foreach (var agent in options.Agents)
                    {
                        if (agent.Chartered)
                        {
                            o.Envelopes.Add(ToEnvelope(agent));
                        }
                    }
                })
                .UseRoleCharterStore<PostgresRoleCharterStore>();

            if (options.Sentinel.Enabled)
            {
                thalos.UseAISentinel(o => ConfigureSentinel(o, options.Sentinel, embeddingGenerator));
            }
        });

        // Only this host sweeps: memories written index_pending (Ollama down, console host, migrated learnings) get embedded
        // here. Registered after AddThalos so hosted-service start order matches the dependency order — the Rag.NET schema
        // initializer runs first, and the sweeper's StartupDelay covers the rest.
        if (options.Memory.Enabled && options.Memory.Reindex.Enabled)
        {
            services.AddHostedService<ReindexPendingMemoriesHostedService>();
        }


        if (options.Workflow.Enabled)
        {
            AddDaedalusWorkflow(
                services, processesRoot, turnDeadline, options.Workflow, options.ToolPolicies,
                configuration.GetSection(WorkflowOutboxDispatchOptions.SectionName));
        }

        // After the workflow block, whose own lease check is the stricter one on a workflow host, so each check's
        // message stays reachable on the host it is written for.
        ValidateChannelOutboxLease(turnDeadline);

        // Off by default: nothing in shipped appsettings sets Thalos:Content:ResyncInterval (see that option's
        // own remarks), so no host pays for this loop until an operator opts in. Registered last, after AddThalos
        // and the optional AddDaedalusWorkflow above, so every service ContentResyncService needs has already
        // been decided one way or the other — including ProcessDefinitionSync, which simply does not exist on
        // this collection when Workflow.Enabled is false; sp.GetService below returns null for it rather than
        // throwing, and ContentResyncService skips that step on every tick instead of failing to resolve.
        if (options.Content.ResyncInterval is { } resyncInterval)
        {
            // SkillSyncService and CharterSyncService are each registered by Thalos only via
            // TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, T>()) — see
            // ForwardHostedServiceToConcreteSingleton's remarks for what resolving them as a bare T would
            // otherwise construct.
            ForwardHostedServiceToConcreteSingleton<SkillSyncService>(services);
            ForwardHostedServiceToConcreteSingleton<CharterSyncService>(services);

            services.AddHostedService(sp => new ContentResyncService(
                resyncInterval,
                sp.GetRequiredService<SkillSyncService>(),
                sp.GetRequiredService<CharterSyncService>(),
                sp.GetService<ProcessDefinitionSync>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<ContentResyncService>>()));
        }

        return services;
    }

    /// <summary>
    ///     Registers the six pieces <c>AddWorkflowOrm</c> (called above, inside <c>AddThalos</c>) does not:
    ///     <see cref="IWorkflowReferenceResolver"/>, <see cref="WorkflowNodeDispatcher"/>, the outbox consumer for
    ///     <see cref="Thalos.Workflow.WorkflowDispatch.TypeName"/>, <see cref="WorkflowRunReconciler"/> on a
    ///     one-minute sweep, <see cref="IProcessDefinitionSource"/> feeding <see cref="ProcessDefinitionSync"/> at
    ///     boot, and the real <see cref="Workflow.ManufactureRunStarter"/> in place of the
    ///     <see cref="Workflow.DisabledManufactureRunStarter"/> registered unconditionally above. Split out from
    ///     <see cref="AddDaedalusAgents(IServiceCollection, IConfiguration, IHostEnvironment, IEmbeddingGenerator{string, Embedding{float}}?)"/> only for readability — every registration here
    ///     depends on <c>IWorkflowStore</c>/<c>IProcessDefinitionStore</c>, which only exist once <c>AddThalos</c>
    ///     has run, so this is called after it, never on its own.
    /// </summary>
    private static void AddDaedalusWorkflow(
        IServiceCollection services, string processesRoot, TimeSpan turnDeadline, WorkflowConfig workflow,
        IEnumerable<ToolPolicyConfig> toolPolicies, IConfigurationSection dispatchSection)
    {
        // IAgentCatalog and ISkillStore both come from AddThalos above. Thalos' own resolver is wrapped in
        // SquadWorkflowReferenceResolver so a process file's `agent:` name goes through SquadAgentResolver
        // first - which is what makes Thalos:Squad:Enabled=false actually collapse implementer and reviewer
        // onto the fallback agent. Wrapped here, at the single registration, rather than inside
        // WorkflowNodeDispatcherFactory: this resolver is deliberately the one lookup shared by
        // ProcessValidator at load time and WorkflowNodeDispatcher at dispatch time, and decorating only one
        // of them would let a process validate against one set of agent names and then run against another.
        services.AddSingleton<IWorkflowReferenceResolver>(sp => new SquadWorkflowReferenceResolver(
            new WorkflowReferenceResolver(
                sp.GetRequiredService<IAgentCatalog>(),
                sp.GetRequiredService<ISkillStore>(),
                sp.GetServices<IWorkflowHostAction>()),
            sp.GetRequiredService<SquadAgentResolver>(),
            sp.GetRequiredService<ILogger<SquadWorkflowReferenceResolver>>()));

        // Where a workflow turn's recall tier is parked between the recall that produced it and the transition
        // that records it. Registered here rather than beside SquadAgentResolver because nothing outside a
        // workflow run ever writes to it: RecallTierRecordingMemoryService only records for a WorkflowCaller.
        services.AddSingleton<WorkflowRecallTierLog>();
        DecorateMemoryServiceWithRecallTierRecording(services);

        // Task B5: the one type that ever writes Thalos:Workflow:StandingInstructionsPath, and only from
        // WorkflowRunGateway's public ResumeAsync overload, on a human's explicit applyStandingInstructions.
        // Registered here, workflow-enabled hosts only, alongside the gateway it is injected into below.
        services.AddSingleton<StandingInstructionsWriter>();

        // The resume/cancel REST boundary's only path to IWorkflowStore (from AddWorkflowOrm above). Never an
        // agent, never a Thalos tool — see WorkflowRunGateway's own remarks for why, and for what actually
        // authorizes a call to it (ASP.NET Core's WorkflowResume policy in Daedalus.Api/Program.cs, not
        // Thalos:ToolPolicies, which DefaultToolAuthorizer only ever evaluates against a tool call).
        services.AddSingleton<WorkflowRunGateway>();

        // Task B6: the host's append-only record of a run, which the write audit and the review lenses append to.
        // A singleton is safe: the store takes a fresh DbContext from IDbContextFactory on every call.
        services.AddSingleton<IWorkflowRunRecordStore, WorkflowRunRecordStore>();

        // Phase 2.8: writes each completed agent node's usage, for cost analytics. Resolves the record store from a scope
        // of its own, like the review lens runner, so this singleton captures nothing scoped.
        services.AddSingleton<NodeUsageRecorder>();

        // Task B13: the post-gate publish step. The resolver above and WorkflowNodeDispatcherFactory both receive every
        // registered IWorkflowHostAction (ruling R27), so this registration is what makes `action: open-pull-request`
        // validate and dispatch. A singleton: it resolves the scoped PR lookup and publisher from a scope of its own.
        services.AddSingleton<IWorkflowHostAction, OpenPullRequestAction>();

        // Phase 2.7: the post-publish step that files deferred review findings. Registered like open-pull-request, so the
        // resolver validates `action: file-review-findings` and the dispatcher runs it.
        services.AddSingleton<IWorkflowHostAction, FileReviewFindingsAction>();

        // Task B7: every workspace write a run is allowed is recorded in that store before the tool runs, or denied.
        DecorateToolAuthorizerWithWriteAudit(services, toolPolicies);

        // Replaces the DisabledManufactureRunStarter registered unconditionally above, now that WorkflowRunStarter
        // (from AddWorkflowOrm, inside AddThalos), IWorkflowReferenceResolver (just above) and IRunWorkspaceProvider
        // (from UseGitWorktreeWorkspaces, inside AddThalos) all resolve.
        // Replace, not TryAdd: TryAddSingleton is first-registration-wins, and the disabled default was already
        // added before this method ever runs.
        services.Replace(ServiceDescriptor.Singleton<IManufactureRunStarter>(sp =>
            new ManufactureRunStarter(
                sp.GetRequiredService<WorkflowRunStarter>(),
                sp.GetRequiredService<IRunWorkspaceProvider>(),
                sp.GetRequiredService<IRunBaseFileReader>(),
                sp.GetRequiredService<WorkflowConfig>())));

        // Phase 2.8: cancels a started run that could not be attached to its task, through WorkflowRunGateway.
        services.Replace(ServiceDescriptor.Singleton<IWorkflowRunCanceller, WorkflowRunCanceller>());

        // Phase 2.8: reads a task's run over the undecorated IWorkflowStore, as WorkflowRunGateway does.
        services.Replace(ServiceDescriptor.Singleton<IWorkflowRunStatusReader, WorkflowRunStatusReader>());

        // ISubagentRunner comes from AddThalos; IWorkflowStore/IProcessDefinitionStore from AddWorkflowOrm above.
        // Built via WorkflowNodeDispatcherFactory, not inline here — see that type's remarks for why this needs
        // the raw ISubagentRunner instead of ISubagentRunExecutor, and why isolating the call there keeps this
        // composition-root class off CleanArchitectureTests' single-seam rule.
        services.AddSingleton(WorkflowNodeDispatcherFactory.Create);

        // The only part of definition handling that is host policy — see FileSystemProcessDefinitionSource's remarks.
        services.AddSingleton<IProcessDefinitionSource>(_ => new FileSystemProcessDefinitionSource(processesRoot));
        services.AddSingleton<ProcessDefinitionSync>();
        services.AddHostedService<ProcessDefinitionSyncHostedService>();

        // Task B9: every csharp-write pattern must reach only run-scoped sources. AddDaedalusAgents checked the patterns
        // and .mcp.json at registration; this checks the sources the container actually built, at host start.
        string[] csharpWritePatterns =
        [
            .. toolPolicies
                .Where(b => string.Equals(b.Policy, CSharpWritePolicy.PolicyName, StringComparison.Ordinal))
                .Select(b => b.Pattern),
        ];
        services.AddHostedService(sp => new CSharpWriteBindingCheck(sp.GetServices<IToolSource>(), csharpWritePatterns));

        // Task B9: before every task node's turn, the run's workspace must exist when the node holds a write grant,
        // and the run's own MCP servers must be ready. Registered on every workflow host, not only one whose .mcp.json
        // declares a runScoped entry: IRunToolServerReadiness is optional, and the workspace check (ruling R9) holds
        // without it. WorkflowNodeDispatcherFactory hands the dispatcher every registered gate.
        // In sandbox mode the gate also records a failed restore once per run (task B5). The ledger that keeps the read
        // to one per run forgets a run when its sandbox is removed or parked, as an observer of the provider; it has no
        // dependencies, so the provider resolving its observers does not reach back to the gate.
        services.AddSingleton<SandboxRestoreLedger>();
        services.AddSingleton<IRunWorkspaceObserver>(sp => sp.GetRequiredService<SandboxRestoreLedger>());
        // The restore reader is the concrete SandboxRunWorkspaceProvider, which only sandbox mode registers, resolved as
        // that type rather than found by testing what IRunWorkspaceProvider resolves to: a decorator on the interface
        // would otherwise turn restore recording off without a word.
        services.AddSingleton<IWorkflowDispatchGate>(sp => new RunToolServersReadyGate(
            sp.GetRequiredService<IRunWorkspaceProvider>(),
            sp.GetRequiredService<WorkflowConfig>(),
            sp.GetService<IRunToolServerReadiness>(),
            sp.GetService<SandboxRunWorkspaceProvider>() is { } sandbox ? sandbox.ReadinessAsync : null,
            sp.GetRequiredService<SandboxRestoreLedger>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RunToolServersReadyGate>>()));

        // A dispatch is, at most, the gate's wait followed by the turn: the gate waits up to RoslynReadyTimeout, so the
        // lease must outlast both, and a run healthily waiting on the gate must not look stranded.
        var gateWait = workflow.RoslynReadyTimeout;
        var dispatchOptions = WorkflowOutboxDispatchOptions.Bind(dispatchSection);
        WorkflowDispatchTiming.Validate(dispatchOptions, turnDeadline, gateWait);
        var strandedAfter = WorkflowDispatchTiming.StrandedAfter(dispatchOptions, turnDeadline, gateWait);

        // The outbox consumer: see WorkflowOutboxDispatchService's remarks for why this is a hand-rolled poller
        // rather than a second ZeroAlloc.Outbox AddOutbox() call. The dispatcher is registered as its concrete
        // type only, never as IOutboxTypeDispatcher: the channel pipeline's OutboxWorkerService enumerates every
        // IOutboxTypeDispatcher in the container, and must not pick this one up.
        // Phase 2.8, amendment A4: writes node-usage records for completions that have none. Registered before the outbox
        // poller below: hosted services start in registration order, so the backfill finishes before a live completion can
        // append the same (run, seq). The unique index on node-usage records settles any overlap regardless.
        services.AddHostedService<NodeUsageBackfill>();
        services.AddSingleton<WorkflowDispatchOutboxDispatcher>();
        services.AddSingleton(dispatchOptions);
        services.AddHostedService(sp => new WorkflowOutboxDispatchService(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<WorkflowDispatchOutboxDispatcher>(),
            sp.GetRequiredService<WorkflowOutboxDispatchOptions>(),
            sp.GetRequiredService<ILogger<WorkflowOutboxDispatchService>>()));

        // The stranded-run sweep: Thalos ships WorkflowRunReconciler with no timer of its own, deliberately.
        services.AddSingleton<WorkflowRunReconciler>();
        services.AddHostedService(sp => new WorkflowStrandedRunSweepService(
            sp.GetRequiredService<WorkflowRunReconciler>(),
            strandedAfter,
            sp.GetRequiredService<ILogger<WorkflowStrandedRunSweepService>>()));

        // Task B10, rulings R14/R19: the only thing that removes a published run's worktree. Over the undecorated
        // IWorkflowStore, like WorkflowRunGateway and WorkflowRunReconciler above — see
        // WorkflowNodeDispatcherFactory.Create's remarks for why only the dispatcher's copy is wrapped. TimeProvider is
        // required, as it is by the write audit and the review lens runner registered on this same host: a host that
        // registers none is a composition error, never a silent fall back to the system clock.
        services.AddSingleton(sp => new RunWorkspaceSweeper(
            sp.GetRequiredService<IRunWorkspaceProvider>(),
            sp.GetRequiredService<IWorkflowStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RunWorkspaceSweeper>>()));
        services.AddHostedService(sp => new RunWorkspaceSweepService(
            sp.GetRequiredService<RunWorkspaceSweeper>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RunWorkspaceSweepService>>()));
    }

    /// <summary>
    ///     Replaces the registered <see cref="IMemoryService"/> with
    ///     <see cref="RecallTierRecordingMemoryService"/> wrapped around it, so every recall a workflow-run turn
    ///     makes leaves its <c>MemoryRecallTier</c> where the run's next transition can record it.
    /// </summary>
    /// <remarks>
    ///     Done by rewriting the descriptor rather than by registering a second <see cref="IMemoryService"/>,
    ///     because Thalos resolves the service by interface in two places -
    ///     <c>MemoryContextProviderSource</c> for auto-recall and <c>MemoryToolSource</c> for the
    ///     <c>memory__*</c> tools - and a last-registration-wins override would leave the inner instance still
    ///     constructible and still resolvable by anything that asked for the concrete type. The inner descriptor
    ///     is rebuilt from whichever form <c>AddThalosMemoryServices</c> used, so this does not silently become
    ///     a no-op if that generator switches between a type, a factory or an instance registration.
    /// </remarks>
    private static void DecorateMemoryServiceWithRecallTierRecording(IServiceCollection services)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IMemoryService));
        if (existing is null)
        {
            // Memory disabled for this host: nothing to wrap, and no tier to record either.
            return;
        }

        services.Remove(existing);
        services.Add(ServiceDescriptor.Describe(
            typeof(IMemoryService),
            sp => new RecallTierRecordingMemoryService(
                (IMemoryService)CreateInner(sp, existing),
                sp.GetRequiredService<WorkflowRecallTierLog>()),
            existing.Lifetime));
    }

    /// <summary>
    ///     The policies whose bound patterns write a run's worktree, so every allowed call to one is audited:
    ///     <c>workspace-write</c> for the <c>workspace__*</c> writes, and <c>csharp-write</c> for Roslyn code actions.
    /// </summary>
    internal static readonly IReadOnlyList<string> WriteAuditedPolicies = [WorkspaceWritePolicy.PolicyName, CSharpWritePolicy.PolicyName];

    /// <summary>
    ///     The host-wide ceiling for the <c>workspace__*</c> tools (ruling R29): the union of every write grant's
    ///     extensions, lower-cased, compared case-insensitively. In local mode each granted caller is narrowed further,
    ///     to its own entry's list, by the write-extensions claim <see cref="WorkflowCaller"/> sets. A grant with no list adds
    ///     nothing, so local mode never sees "any"; S6 refuses such a grant at boot without the sandbox anyway.
    /// </summary>
    internal static HashSet<string> WriteExtensionCeiling(IEnumerable<WriteGrantConfig> grants) =>
        grants.SelectMany(g => g.AllowedExtensions ?? [])
            .Where(e => AllowedExtensionPattern().IsMatch(e))
            .Select(e => string.Create(e.Length, e, static (target, source) => System.Text.Ascii.ToLower(source, target, out _)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The sandbox's host-wide write ceiling: null (any extension) when any grant lists no extensions, which S6 allows
    ///     only with the sandbox enabled; otherwise the union of the grants' lists, as <see cref="WriteExtensionCeiling"/>
    ///     computes it. Nothing narrows it per node: inside the sandbox every write runs as the sandbox's own caller,
    ///     with no write-extensions claim, so this ceiling is all a run is held to. That is why
    ///     <c>ValidateSandboxConfig</c> requires every grant to list the same extensions, or none (Thalos issue #251).
    /// </summary>
    internal static IReadOnlySet<string>? WriteExtensionCeilingOrAny(IEnumerable<WriteGrantConfig> grants)
    {
        var list = grants.ToList();
        return list.Exists(g => g.AllowedExtensions is null) ? null : WriteExtensionCeiling(list);
    }

    /// <summary>
    ///     The protected path entries a run gets on top of Thalos's <see cref="SandboxOptions.DefaultProtectedPaths"/>:
    ///     the configured extras of <see cref="SandboxConfig.ProtectedPaths"/>, then the standing-instructions file, as a
    ///     file and as a directory. Sandbox mode passes only these, because <see cref="SandboxOptions"/> always adds its
    ///     defaults itself; local mode passes the defaults too, so both modes protect the same set from one source.
    /// </summary>
    /// <remarks>
    ///     <b>Why the directory entry.</b> <see cref="ProtectedPathSet"/> matches an entry without a trailing <c>/</c>
    ///     exactly, so the file entry alone leaves <c>AGENT.md/x</c> writable. Publish would then sweep such a path into
    ///     the approved standing-instructions commit: the code commit excludes the pathspec <c>AGENT.md</c>, which
    ///     matches everything under a directory of that name, and the path-scoped standing-instructions commit adds
    ///     it. The <c>&lt;path&gt;/</c> entry protects the name as a directory too. The root fix is Thalos issue #265,
    ///     a protected file entry that also covers the name used as a directory; this entry stays correct after it.
    /// </remarks>
    internal static IEnumerable<string> ExtraProtectedPaths(WorkflowConfig workflow, string standingInstructionsPath) =>
        [.. workflow.Sandbox.ProtectedPaths, standingInstructionsPath, standingInstructionsPath + "/"];

    /// <summary>
    ///     Local mode, phase 2.5's: every run gets a git worktree of its repository under the data root, and the
    ///     <c>workspace__*</c> tools serve it in-process. The ceiling is the union of every write grant's extensions;
    ///     each granted caller is narrowed to its own entry's list through the write-extensions claim
    ///     <see cref="WorkflowCaller"/> sets (ruling R29). The protected set is the sandbox's: Thalos's defaults, the
    ///     configured extras and the standing-instructions file, which stays readable, never writable, by any run.
    /// </summary>
    private static void ConfigureLocalMode(ThalosBuilder thalos, WorkflowConfig workflow, string dataRoot, string standingInstructionsPath)
    {
        thalos.UseGitWorktreeWorkspaces(o => o.DataRoot = dataRoot);
        thalos.UseRunWorkspaceTools(
            WriteExtensionCeiling(workflow.WriteGrants),
            o =>
            {
                foreach (var path in SandboxOptions.DefaultProtectedPaths.Concat(ExtraProtectedPaths(workflow, standingInstructionsPath)))
                {
                    o.ProtectedPaths.Add(path);
                }
            });
    }

    /// <summary>
    ///     Sandbox mode, phase 2.6: every run gets its own Docker container. <c>UseSandboxRunWorkspaces</c> registers the
    ///     remote <c>workspace</c> and <c>sandbox</c> tool sources and replaces the run workspace provider and its
    ///     companions, so neither <c>UseGitWorktreeWorkspaces</c> nor <c>UseRunWorkspaceTools</c> is called: local
    ///     workspace tools would serve a run's writes on the host. It also registers the <see cref="IRunWorkspaceGit"/>
    ///     publish commits and pushes with, over the trusted publish worktrees under <c>&lt;DataRoot&gt;/publish</c>
    ///     (Thalos 0.14.1, issue #250), so nothing is added here.
    /// </summary>
    private static void ConfigureSandboxMode(ThalosBuilder thalos, WorkflowConfig workflow, string dataRoot, string standingInstructionsPath)
    {
        var sandbox = workflow.Sandbox;
        thalos.UseDockerSandboxRuntime(d => ConfigureDockerSandbox(d, sandbox.Docker));
        thalos.UseSandboxRunWorkspaces(o =>
        {
            o.DataRoot = dataRoot;
            o.Image = sandbox.Image;
            o.Limits = new SandboxLimits(sandbox.Cpus, sandbox.MemoryMb * 1024L * 1024, sandbox.Pids);
            o.AllowedWriteExtensions = WriteExtensionCeilingOrAny(workflow.WriteGrants);
            foreach (var path in ExtraProtectedPaths(workflow, standingInstructionsPath))
            {
                o.ProtectedPaths.Add(path);
            }
        });

        // Task B6: the run's sandbox__test and sandbox__build calls become test-result run records, which the pull
        // request body states. Sandbox mode only: local mode has no sandbox tools. Over the undecorated IWorkflowStore,
        // as the sweeper is, since it reads only the run's current node and sequence number.
        thalos.Services.AddSingleton<IRunToolCallObserver>(sp => new SandboxCallRecorder(
            sp.GetRequiredService<IWorkflowStore>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SandboxCallRecorder>>()));
    }

    /// <summary>
    ///     Sets <see cref="DockerSandboxOptions"/> from <c>Thalos:Workflow:Sandbox:Docker</c>. The gateway and egress
    ///     container names are derived from the network: with Thalos's fixed defaults, two hosts on different networks
    ///     sharing one engine would take over each other's infrastructure containers.
    /// </summary>
    internal static void ConfigureDockerSandbox(DockerSandboxOptions d, SandboxDockerConfig docker)
    {
        d.Endpoint = docker.Endpoint is { Length: > 0 } e ? new Uri(e) : null;
        d.InternalNetwork = docker.Network;
        d.GatewayContainerName = $"{docker.Network}-gateway";
        d.EgressContainerName = $"{docker.Network}-egress";
        d.GatewayImage = docker.GatewayImage;
        d.EgressImage = docker.EgressImage;
        d.GatewayPort = docker.GatewayPort;
    }

    /// <summary>
    ///     Fails fast when a <c>Thalos:ToolPolicies</c> pattern bound to <see cref="CSharpWritePolicy"/> does not start
    ///     with a literal <c>&lt;source&gt;__</c>, or reaches an MCP server in <paramref name="mcpConfigPath"/> that is
    ///     not run-scoped. That policy admits a granted workflow caller, and only a run-scoped source is guaranteed to
    ///     serve such a caller from its own run's server: a host-scoped one would apply its code actions to this host's
    ///     own solution. <see cref="CSharpWriteBindingCheck"/> says which sources a pattern reaches, and checks the
    ///     sources the container builds, at host start, for any not declared in <c>.mcp.json</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     A binding does not start with a literal source name, or reaches a server that is not run-scoped.
    /// </exception>
    internal static void ValidateCSharpWriteBindings(IEnumerable<ToolPolicyConfig> toolPolicies, string mcpConfigPath)
    {
        var patterns = toolPolicies
            .Where(b => string.Equals(b.Policy, CSharpWritePolicy.PolicyName, StringComparison.Ordinal))
            .Select(b => b.Pattern)
            .ToList();
        var hostScoped = File.Exists(mcpConfigPath)
            ? McpConfigFile.Load(mcpConfigPath).Where(s => s.Value.RunScoped is null).Select(s => s.Key).ToList()
            : [];
        foreach (var pattern in patterns)
        {
            var reached = CSharpWriteBindingCheck.SourcesReachedBy(pattern)
                ?? throw new InvalidOperationException(CSharpWriteBindingCheck.NotLiteral(pattern));
            if (hostScoped.FirstOrDefault(name => reached.Contains(name, StringComparer.Ordinal)) is { } server)
            {
                throw new InvalidOperationException(
                    $"Thalos:ToolPolicies binds '{pattern}' to {CSharpWritePolicy.PolicyName}, which reaches MCP server " +
                    $"'{server}' in '{mcpConfigPath}', and that server is not run-scoped. The policy lets a granted " +
                    "workflow run call the tool, and only a runScoped server serves a run from its own worktree; make the " +
                    "server run-scoped or bind the pattern to developer instead.");
            }
        }
    }

    /// <summary>
    ///     Fails fast when the resolved workspace data root contains <c>%</c>. Every run's worktree, and so every
    ///     <c>${run.workspace.root}</c> and <c>${run.workspace.solution}</c> a run-scoped MCP server is started with, lies
    ///     under it, and on Windows the MCP SDK starts a stdio server through <c>cmd.exe</c>, which would expand a
    ///     <c>%NAME%</c> in it; Thalos refuses such a start, which would fail every run at its first gate. Checked on every
    ///     OS so a configuration that works on one works on the others.
    /// </summary>
    /// <exception cref="InvalidOperationException">The data root contains <c>%</c>.</exception>
    internal static void ValidateDataRootForRunServers(string resolvedDataRoot)
    {
        if (resolvedDataRoot.Contains('%', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{WorkflowConfig.SectionName}:DataRoot resolves to '{resolvedDataRoot}', which contains '%'. A run's " +
                "run-scoped MCP servers are started with paths under it, and cmd.exe would expand the '%' on Windows. " +
                "Set DataRoot to an absolute path without '%'.");
        }
    }

    /// <summary>
    ///     Replaces the registered <see cref="IToolAuthorizer"/> with <see cref="AuditingToolAuthorizer"/> wrapped around
    ///     it, auditing every <c>Thalos:ToolPolicies</c> pattern bound to a policy in <see cref="WriteAuditedPolicies"/>.
    /// </summary>
    /// <remarks>
    ///     The descriptor is rewritten, as in <see cref="DecorateMemoryServiceWithRecallTierRecording"/>, so the one
    ///     authorizer every <c>AuthorizingAIFunction</c> resolves is the auditing one. It wraps the authorizer
    ///     <c>AddThalos</c> registered, which is the only one: AI.Sentinel screens the chat pipeline and does not
    ///     decorate <see cref="IToolAuthorizer"/>, so the audit sees the final decision. A host with no authorizer to
    ///     wrap is a composition error, not a host with nothing to audit, so it throws rather than skip the audit.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No <see cref="IToolAuthorizer"/> is registered.</exception>
    private static void DecorateToolAuthorizerWithWriteAudit(IServiceCollection services, IEnumerable<ToolPolicyConfig> toolPolicies)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IToolAuthorizer))
            ?? throw new InvalidOperationException("No IToolAuthorizer is registered to audit; AddThalos registers one.");
        string[] auditedPatterns =
        [
            .. toolPolicies
                .Where(b => WriteAuditedPolicies.Contains(b.Policy, StringComparer.Ordinal))
                .Select(b => b.Pattern),
        ];

        services.Remove(existing);
        services.Add(ServiceDescriptor.Describe(
            typeof(IToolAuthorizer),
            sp => new AuditingToolAuthorizer(
                (IToolAuthorizer)CreateInner(sp, existing),
                auditedPatterns,
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<AuditingToolAuthorizer>>()),
            existing.Lifetime));
    }

    /// <summary>
    ///     Builds the service a rewritten descriptor wraps, from whichever form it was registered in: an instance, a
    ///     factory or a type.
    /// </summary>
    private static object CreateInner(IServiceProvider sp, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(sp)
        ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);

    /// <summary>
    ///     Ensures <typeparamref name="T"/> is resolvable as a concrete singleton, and that it is the exact
    ///     instance the host's <see cref="IHostedService"/> pipeline runs — not a second one constructed
    ///     independently.
    /// </summary>
    /// <remarks>
    ///     Reading <c>SkillThalosBuilderExtensions</c>/<c>CharterThalosBuilderExtensions</c> at Thalos.NET v0.10.0
    ///     confirms <see cref="Thalos.Skills.SkillSyncService"/> and
    ///     <see cref="Thalos.Skills.Charters.CharterSyncService"/> are each registered only via
    ///     <c>TryAddEnumerable(ServiceDescriptor.Singleton&lt;IHostedService, T&gt;())</c> — there is no concrete
    ///     <c>T</c> registration alongside it. A container caches a singleton per <see cref="ServiceDescriptor"/>,
    ///     not per implementation type, so a plain <c>services.TryAddSingleton&lt;T&gt;()</c> here would add a
    ///     second, independently-constructed instance: resolving <c>IEnumerable&lt;IHostedService&gt;</c> (what
    ///     <c>IHost.StartAsync</c> does, to run <c>StartingAsync</c> once at boot) and resolving <c>T</c> directly
    ///     (what <see cref="ContentResyncService"/> does, on every tick) would each get their own sync racing the
    ///     same store. This method's second half prevents that: it finds the descriptor Thalos added, removes it,
    ///     and replaces it with a factory that forwards to the concrete singleton, so both paths resolve the one
    ///     instance. Idempotent — a second call for the same <typeparamref name="T"/> finds no matching descriptor
    ///     left to remove and leaves the singleton registration as-is.
    /// </remarks>
    private static void ForwardHostedServiceToConcreteSingleton<T>(IServiceCollection services)
        where T : class, IHostedService
    {
        services.TryAddSingleton<T>();

        var hostedDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T));
        if (hostedDescriptor is null)
        {
            return;
        }

        services.Remove(hostedDescriptor);
        services.Add(ServiceDescriptor.Singleton<IHostedService>(sp => sp.GetRequiredService<T>()));
    }

    /// <summary>
    ///     Rejects a turn deadline the channel and scheduling outbox's lease cannot hold: the lease is renewed only
    ///     right before a dispatch, so a scheduling step whose agent turn outlived it could be claimed and run a
    ///     second time by the other host polling the same table.
    /// </summary>
    private static void ValidateChannelOutboxLease(TimeSpan turnDeadline)
    {
        if (turnDeadline >= ChannelOutboxServiceCollectionExtensions.LeaseDuration)
        {
            throw new InvalidOperationException(
                $"DetachedRuns:DeadlineSeconds ({turnDeadline}) must be shorter than the channel and scheduling " +
                $"outbox's lease ({ChannelOutboxServiceCollectionExtensions.LeaseDuration}): a scheduling step whose " +
                "agent turn outlives the lease can be claimed and run again by another host.");
        }
    }

    /// <summary>
    ///     Fails fast on <c>Thalos:Content</c>: a configured, non-positive <see cref="ContentConfig.ResyncInterval"/>
    ///     would otherwise surface much later, as <see cref="PeriodicTimer"/>'s own
    ///     <see cref="ArgumentOutOfRangeException"/> the first time <see cref="ContentResyncService.ExecuteAsync"/>
    ///     runs, rather than at host start.
    /// </summary>
    private static void ValidateContentConfig(ContentConfig config)
    {
        if (config.ResyncInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{ContentConfig.SectionName}:ResyncInterval must be greater than zero when set, but was " +
                $"{interval.ToString(null, CultureInfo.InvariantCulture)}.");
        }
    }

    /// <summary>
    ///     Fails fast on the phase 2.5 write-authority keys of <c>Thalos:Workflow</c>: <c>Repositories</c>,
    ///     <c>DataRoot</c>, <c>CommitAuthor</c> and <c>WriteGrants</c>. Together they decide what a run may check out,
    ///     where it writes, and which files a model turn may change, so a malformed value must stop the host at
    ///     registration rather than surface at the first run.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     A repository name is malformed or duplicated, a remote is blank, a solution path is blank, rooted or contains
    ///     <c>..</c> or <c>%</c>, <c>DataRoot</c> is set but not absolute, <c>RoslynReadyTimeout</c> is not positive, a write grant
    ///     has a blank process or node or repeats another grant's process and node, a write grant lists no extensions or a
    ///     malformed one, a write grant leaves its extensions out without the sandbox (S6), the sandbox is enabled
    ///     without its images or with a repository that names no solution, or repositories are configured without a full
    ///     commit author.
    /// </exception>
    /// <param name="config">The bound <c>Thalos:Workflow</c> section.</param>
    /// <param name="rawGrants">
    ///     The raw <c>Thalos:Workflow:WriteGrants</c> section, read only to tell an explicit empty
    ///     <c>AllowedExtensions</c> from a missing one (ruling R48).
    /// </param>
    private static void ValidateWorkflowWriteConfig(WorkflowConfig config, IConfigurationSection rawGrants)
    {
        const string section = WorkflowConfig.SectionName;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < config.Repositories.Count; i++)
        {
            var repository = config.Repositories[i];
            var key = $"{section}:Repositories:{i}";
            if (!RepositoryNamePattern().IsMatch(repository.Name))
            {
                throw new InvalidOperationException(
                    $"{key}:Name '{repository.Name}' must match ^[a-z0-9][a-z0-9-]{{0,63}}$. It becomes a path segment " +
                    "under the data root and is the name a run start refers to.");
            }

            if (!names.Add(repository.Name))
            {
                throw new InvalidOperationException(
                    $"{key}:Name '{repository.Name}' is declared more than once. A run start names its repository, so " +
                    "two entries with one name would make the target ambiguous.");
            }

            if (string.IsNullOrWhiteSpace(repository.Remote))
            {
                throw new InvalidOperationException(
                    $"{key}:Remote must not be blank: it is the git remote the run's worktree is cloned from and pushed to.");
            }

            if (repository.Solution is { } blankSolution && string.IsNullOrWhiteSpace(blankSolution))
            {
                throw new InvalidOperationException(
                    $"{key}:Solution must name a solution file or be left out: a blank value would hand the run's " +
                    "Roslyn server the worktree directory itself.");
            }

            if (repository.Solution is { } solution
                && (Path.IsPathRooted(solution) || solution.Contains("..", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"{key}:Solution '{solution}' must be a repository-relative path with no '..': the run's Roslyn " +
                    "server loads it from inside the worktree and nowhere else.");
            }

            if (repository.Solution is { } percentSolution && percentSolution.Contains('%', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{key}:Solution '{percentSolution}' must not contain '%': the run's Roslyn server is started with " +
                    "its path, and cmd.exe would expand the '%' on Windows.");
            }
        }

        if (!string.IsNullOrEmpty(config.DataRoot) && !Path.IsPathFullyQualified(config.DataRoot))
        {
            throw new InvalidOperationException(
                $"{section}:DataRoot '{config.DataRoot}' must be an absolute path, or blank for " +
                "%LOCALAPPDATA%/Daedalus/workflow-data. A relative data root would move with the working directory.");
        }

        if (config.RoslynReadyTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{section}:RoslynReadyTimeout must be greater than zero, but was " +
                $"{config.RoslynReadyTimeout.ToString(null, CultureInfo.InvariantCulture)}. A run waits this long for its " +
                "Roslyn server, so zero or less would fail every run before the server could start.");
        }

        var grantedNodes = new HashSet<(string Process, string Node)>();
        for (var i = 0; i < config.WriteGrants.Count; i++)
        {
            var grant = config.WriteGrants[i];
            var key = $"{section}:WriteGrants:{i}";
            if (string.IsNullOrWhiteSpace(grant.Process))
            {
                throw new InvalidOperationException($"{key}:Process must not be blank: a write grant names the process it applies to.");
            }

            if (string.IsNullOrWhiteSpace(grant.Node))
            {
                throw new InvalidOperationException($"{key}:Node must not be blank: a write grant names the node it applies to.");
            }

            if (!grantedNodes.Add((grant.Process, grant.Node)))
            {
                throw new InvalidOperationException(
                    $"{key} grants '{grant.Process}'/'{grant.Node}' more than once. Only one grant may apply to a node, " +
                    "or which extension list wins would depend on the order of the entries.");
            }

            // Ruling R48: the binder reads an explicit "AllowedExtensions": [] exactly as a missing key, null, which means
            // any extension. The JSON provider keeps the two apart, an empty array being the key with an empty value, so
            // the raw section decides: a key that is present but holds no list fails closed.
            if (grant.AllowedExtensions is null && rawGrants.GetSection($"{i}:AllowedExtensions") is { Value: not null })
            {
                throw new InvalidOperationException(
                    $"{key}:AllowedExtensions is present but lists no extensions. An empty list would make nothing " +
                    "writable, so it is refused; list the extensions this node may write, or remove the key to allow any " +
                    $"extension, which needs {section}:Sandbox:Enabled.");
            }

            if (grant.AllowedExtensions is not { } extensions)
            {
                // S6: with no list the ceiling is "any", so a run may write project, props and targets files. Only a run
                // sandbox keeps MSBuild's evaluation of them off the host.
                if (!config.Sandbox.Enabled)
                {
                    throw new InvalidOperationException(
                        $"{section}:WriteGrants entry '{grant.Process}/{grant.Node}' allows every extension, which is only " +
                        $"safe inside a run sandbox; set {section}:Sandbox:Enabled or list AllowedExtensions.");
                }

                continue;
            }

            if (extensions.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{key}:AllowedExtensions must list at least one extension, such as \".cs\", or be left out under a " +
                    "run sandbox. It is an allow-list (ruling R29), and an empty one is not a supported configuration.");
            }

            if (extensions.FirstOrDefault(e => !AllowedExtensionPattern().IsMatch(e)) is { } malformed)
            {
                throw new InvalidOperationException(
                    $"{key}:AllowedExtensions entry '{malformed}' must be a dot followed by letters and digits only, " +
                    "such as \".cs\" (ruling R29).");
            }
        }

        ValidateSandboxConfig(config);

        if (config.Repositories.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(config.CommitAuthor.Name))
            {
                throw new InvalidOperationException(
                    $"{section}:CommitAuthor:Name must not be blank while {section}:Repositories is configured: " +
                    "every run commit is written as this author.");
            }

            if (string.IsNullOrWhiteSpace(config.CommitAuthor.Email))
            {
                throw new InvalidOperationException(
                    $"{section}:CommitAuthor:Email must not be blank while {section}:Repositories is configured: " +
                    "every run commit is written as this author.");
            }
        }
    }

    /// <summary>
    ///     Fails fast on <c>Thalos:Workflow:Sandbox</c> when it is enabled, in Daedalus's words and before Thalos's own
    ///     registration checks: the run image and both infrastructure images must be set, and every repository must name
    ///     its solution, because a sandboxed run's Roslyn server is started on it (ruling R33). A disabled sandbox is not
    ///     checked: nothing reads it. The data root needs no check here: <c>ValidateWorkflowWriteConfig</c> already
    ///     refuses a relative one, and a blank one resolves to an absolute default.
    /// </summary>
    private static void ValidateSandboxConfig(WorkflowConfig config)
    {
        const string section = WorkflowConfig.SectionName + ":Sandbox";
        var sandbox = config.Sandbox;
        if (!sandbox.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(sandbox.Image))
        {
            throw new InvalidOperationException(
                $"{section}:Image must not be blank while the sandbox is enabled: it is the image every run's container is created from.");
        }

        if (string.IsNullOrWhiteSpace(sandbox.Docker.GatewayImage) || string.IsNullOrWhiteSpace(sandbox.Docker.EgressImage))
        {
            throw new InvalidOperationException(
                $"{section}:Docker:GatewayImage and {section}:Docker:EgressImage must not be blank while the sandbox is " +
                "enabled: they run the gateway every run's tools are reached through and the only proxy a run reaches the network by.");
        }

        if (config.Repositories.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Solution)) is { } unsolved)
        {
            throw new InvalidOperationException(
                $"{WorkflowConfig.SectionName}:Repositories entry '{unsolved.Name}' must name its Solution while the sandbox " +
                "is enabled: a sandboxed run's Roslyn server is started on that solution inside the run's container.");
        }

        // Inside a run sandbox every workspace__* call runs as the sandbox's own caller, which carries the run id and no
        // write-extensions claim, under the host-wide ceiling alone; the host forwards a call without checking the
        // caller's claim. So a grant cannot narrow its own node there, and grants with different lists would silently
        // all get the widest. Thalos issue #251 tracks per-node narrowing in the sandbox.
        if (config.WriteGrants.Count > 1)
        {
            var first = config.WriteGrants[0];
            var firstSet = ExtensionSetOf(first);
            if (config.WriteGrants.Skip(1).FirstOrDefault(g => !SameExtensions(firstSet, ExtensionSetOf(g))) is { } other)
            {
                throw new InvalidOperationException(
                    $"{WorkflowConfig.SectionName}:WriteGrants entries '{first.Process}/{first.Node}' and " +
                    $"'{other.Process}/{other.Node}' list different AllowedExtensions, but per-node narrowing is not " +
                    "available inside a run sandbox (Thalos issue #251): every sandboxed write is held to the host-wide " +
                    "ceiling alone. Give every grant the same AllowedExtensions, or leave the key out of every grant.");
            }
        }
    }

    /// <summary>A grant's extensions as the ceiling compares them, or <see langword="null"/> for a grant with no list.</summary>
    private static HashSet<string>? ExtensionSetOf(WriteGrantConfig grant) =>
        grant.AllowedExtensions is null ? null : WriteExtensionCeiling([grant]);

    private static bool SameExtensions(HashSet<string>? a, HashSet<string>? b) =>
        a is null || b is null ? a is null && b is null : a.SetEquals(b);

    /// <summary>
    ///     The workspace data root: the configured <c>Thalos:Workflow:DataRoot</c> when set, which
    ///     <see cref="ValidateWorkflowWriteConfig"/> has already checked is absolute, otherwise
    ///     <c>%LOCALAPPDATA%/Daedalus/workflow-data</c>.
    /// </summary>
    internal static string ResolveDataRoot(string configured) =>
        string.IsNullOrEmpty(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Daedalus", "workflow-data")
            : configured;

    // \z rather than $: in .NET, $ also matches just before a trailing newline, so "sandbox\n" would pass.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)] // MA0009: timeout (the pattern is linear)
    private static partial Regex RepositoryNamePattern();

    // \z rather than $, for the same reason: ".cs\n" is not an extension.
    [GeneratedRegex(@"^\.[A-Za-z0-9]+\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)] // MA0009: timeout (the pattern is linear)
    // Internal because WorkflowCaller filters a grant's extensions with this same pattern before they reach the
    // write-extension claim.
    internal static partial Regex AllowedExtensionPattern();

    /// <summary>
    ///     Memory-only registration for hosts that run Ralph but <b>no</b> Thalos agents — the console worker. Registers the
    ///     same <c>IMemoryService</c>, Postgres store and Rag.NET index as <see cref="AddDaedalusAgents(IServiceCollection, IConfiguration, IHostEnvironment, IEmbeddingGenerator{string, Embedding{float}}?)"/>, plus the Ralph
    ///     port <c>ILearningsMemory</c>. No agents, tools, Sentinel or reindex service (the API host runs that one).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Host configuration; <c>Thalos:Memory</c> and <c>ConnectionStrings:daedalus</c> are read.</param>
    /// <remarks>
    ///     <para>
    ///         Mutually exclusive with <see cref="AddDaedalusAgents(IServiceCollection, IConfiguration, IHostEnvironment, IEmbeddingGenerator{string, Embedding{float}}?)"/>, and checked: calling both throws. The Daedalus
    ///         registrations are <c>TryAdd</c>-based, but <c>UseRagNetMemory</c> is last-call-wins, so a later
    ///         <see cref="AddDaedalusMemory"/> would flip <c>EnsureSchemaOnStartup</c> back to <c>false</c> on the API host
    ///         — nobody would create <c>rag_chunks</c>, every memory would stay <c>index_pending</c>, and nothing would fail
    ///         loudly.
    ///     </para>
    ///     <para>
    ///         No skills either: the Ralph worker runs no Thalos agents, so there is no catalogue to build.
    ///     </para>
    ///     <para>
    ///         <b>The API host owns the Rag.NET schema.</b> This call sets <c>EnsureSchemaOnStartup = false</c>, because the
    ///         AppHost starts the API and the console concurrently against one database and two racing
    ///         <c>CREATE EXTENSION</c>/<c>CREATE TABLE</c>/<c>CREATE INDEX</c> sweeps can fail on the pg catalog and take a
    ///         host down. Until the API has created <c>rag_chunks</c>, this host degrades along the designed path: memories
    ///         are stored <c>index_pending</c> and the API's reindex sweeper embeds them afterwards.
    ///     </para>
    ///     <para>
    ///         Requires <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c>. Register an <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>
    ///         before this call for a working index; without one memories are likewise stored <c>index_pending</c>.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     A <c>Thalos:Memory</c> value is out of range, or memory is already registered on this collection — this method
    ///     and <see cref="AddDaedalusAgents(IServiceCollection, IConfiguration, IHostEnvironment, IEmbeddingGenerator{string, Embedding{float}}?)"/> are mutually exclusive.
    /// </exception>
    public static IServiceCollection AddDaedalusMemory(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ThrowIfMemoryAlreadyRegistered(services, nameof(AddDaedalusMemory));

        var options = new DaedalusAgentsOptions();
        configuration.GetSection(DaedalusAgentsOptions.SectionName).Bind(options);
        var connectionString = ResolveConnectionString(configuration);

        ValidateMemoryConfig(options.Memory);
        services.TryAddSingleton(options.Memory);
        services.TryAddSingleton(options.Memory.RalphRecall);
        services.TryAddSingleton<ILearningsMemory, ThalosLearningsMemory>();
        services.AddThalos(thalos => ConfigureMemory(
            thalos, configuration.GetSection(MemoryConfig.SectionName), options.Memory, connectionString, ensureSchema: false));
        return services;
    }

    /// <summary>
    ///     Registers the GitHub seam behind <see cref="DaedalusRepoTools"/> and <see cref="DaedalusRepoActionTools"/>
    ///     by delegating to <c>Daedalus.Infrastructure.Extensions.InfrastructureServiceExtensions.AddGitHubApi</c>:
    ///     <see cref="GitHubOptions"/> from <see cref="GitHubOptions.SectionName"/>, the token source, and one
    ///     <see cref="GitHubApi"/> exposed as both halves of the read/write split.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The token is not configuration.</b> <see cref="GitHubTokenSource"/> reads
    ///         <c>GITHUB_TOKEN</c> from the environment, so nothing bound here ever carries a credential and no
    ///         token can be committed. <c>ExternalServices:Platforms:GitHub:AuthToken</c> exists for the older
    ///         Infrastructure platform clients and is deliberately left alone — <see cref="GitHubOptions"/> does
    ///         not bind it.
    ///     </para>
    ///     <para>
    ///         Both interfaces resolve the same concrete type, but each tool class can only reach its own half:
    ///         <see cref="DaedalusRepoTools"/> injects <see cref="IGitHubReader"/> and
    ///         <see cref="DaedalusRepoActionTools"/> injects <see cref="IGitHubWriter"/>, so a read-only tool has
    ///         no path to a write however it is called.
    ///     </para>
    /// </remarks>
    private static void AddGitHub(IServiceCollection services, IConfiguration configuration)
    {
        // GitHubApi now lives in Daedalus.Infrastructure (Agents already references Infrastructure, and this is
        // what lets Daedalus.Infrastructure.Services.CodeAnalysis.GitHubPullRequestFactory delegate to it directly
        // without a circular project reference). AddGitHubApi is idempotent, so a host that also calls
        // AddCodeAnalysisServices (which registers the same client for PR creation) does not double-register it.
        services.AddGitHubApi(configuration);
    }

    private static string ResolveConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(DatabaseConnectionName) ?? DatabaseSettings.GetDefaultConnectionString();

    /// <summary>
    ///     Fails fast on <c>Thalos:Squad</c>. A blank <see cref="SquadOptions.FallbackAgentName"/> is rejected in
    ///     <em>both</em> modes, because it is the value the rollback needs and a rollback that cannot be taken is
    ///     not a rollback.
    /// </summary>
    /// <remarks>
    ///     <b>What a blank value actually does, which is why this is a boot failure rather than a warning.</b>
    ///     <see cref="SquadAgentResolver.Resolve"/> guards its input and then returns
    ///     <see cref="SquadOptions.FallbackAgentName"/> unchecked, and Thalos'
    ///     <c>WorkflowReferenceResolver.ResolveAgentIdAsync</c> opens with <c>ThrowIfNullOrWhiteSpace</c>. So a
    ///     blank name does not resolve to "no agent" — it throws <see cref="ArgumentException"/>. At host start
    ///     that throw comes out of <c>ProcessDefinitionSync</c> validating <c>manufacture.yaml</c>, escapes
    ///     <c>ProcessDefinitionSyncHostedService.StartAsync</c>, and takes the whole host down with a message
    ///     naming a parameter rather than a configuration key. Past load it escapes
    ///     <c>WorkflowNodeDispatcher.ResolveAgentAsync</c>, which deliberately does not catch, so the dispatch
    ///     message returns to the outbox and retries — the opposite of the clean <c>FailAsync</c> every other
    ///     unresolvable agent name gets.
    ///     <para>
    ///     The reachable path is not a typo but the natural rollback: an operator deletes the <c>"Squad"</c>
    ///     block instead of flipping one boolean. <see cref="SquadOptions.Enabled"/> then defaults to
    ///     <see langword="false"/> and <see cref="SquadOptions.FallbackAgentName"/> to <c>""</c>, which is
    ///     exactly the combination that throws. Validating only when the squad is off would leave the same trap
    ///     one step away — a host running with the squad on and no fallback boots clean and breaks the moment
    ///     anyone rolls it back — so the value is required in both modes.
    ///     </para>
    /// </remarks>
    private static void ValidateSquadConfig(SquadOptions config)
    {
        if (string.IsNullOrWhiteSpace(config.FallbackAgentName))
        {
            throw new InvalidOperationException(
                $"{SquadOptions.SectionName}:FallbackAgentName must name a real Thalos:Agents entry and must not be blank. " +
                "Every workflow role collapses onto it when Thalos:Squad:Enabled is false, and a blank name throws " +
                "ArgumentException out of agent resolution rather than failing the run cleanly - at host start that takes " +
                "the host down, and past start it dead-letters the dispatch. Deleting the whole Thalos:Squad block leaves " +
                "this blank, so roll the squad back by setting Enabled to false and keeping the fallback name.");
        }
    }

    /// <summary>
    ///     Fails fast when a chartered agent is still defined in both places at once. A chartered agent's
    ///     <see cref="AgentConfig.Instructions"/> must be blank — its prose now comes from
    ///     <c>roles/&lt;Name&gt;.md</c> — and its <see cref="AgentConfig.Tools"/> must not be empty. Unlike an
    ///     unchartered agent (<see cref="ToDefinition"/>), a chartered one gets no <c>["*"]</c> fallback for an
    ///     empty <c>Tools</c> list: the tool list is this role's whole security envelope, and the reviewer's
    ///     independence rests on that envelope never silently widening to everything.
    /// </summary>
    /// <exception cref="InvalidOperationException">A chartered agent still has non-blank <c>Instructions</c>, or an empty <c>Tools</c> list.</exception>
    private static void ValidateCharterConfig(DaedalusAgentsOptions options)
    {
        foreach (var agent in options.Agents)
        {
            if (!agent.Chartered)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(agent.Instructions))
            {
                throw new InvalidOperationException(
                    $"Thalos:Agents: chartered agent '{agent.Name}' still has non-blank Instructions. " +
                    "A chartered agent's prose comes from its role charter (roles/<Name>.md) now - delete " +
                    "Instructions from this entry.");
            }

            if (agent.Tools.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Thalos:Agents: chartered agent '{agent.Name}' has an empty Tools list. " +
                    "A chartered agent's Tools is its whole security envelope and has no wildcard default - set it explicitly.");
            }
        }
    }

    /// <summary>
    ///     The single default root, applied here rather than as <see cref="DaedalusAgentsOptions.CharterRoots"/>'s
    ///     own field initializer — see that property's remarks for why a pre-populated default there is
    ///     unusable with <c>ConfigurationBinder</c>.
    /// </summary>
    private static readonly IReadOnlyList<string> DefaultCharterRoots = ["roles"];

    /// <summary>
    ///     Resolves <c>Thalos:CharterRoots</c> against the content root, the same way <see cref="ResolveSkillRoots"/>
    ///     resolves <c>Thalos:Skills:Roots</c> — see <see cref="ResolveContentRoot"/>'s remarks for why the
    ///     assembly-directory fallback is load-bearing under <c>dotnet run</c>/Aspire. Falls back to
    ///     <see cref="DefaultCharterRoots"/> when <paramref name="roots"/> binds empty.
    /// </summary>
    private static IReadOnlyList<string> ResolveCharterRoots(IList<string> roots, IHostEnvironment environment)
    {
        var configured = roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (configured.Count == 0)
        {
            configured = [.. DefaultCharterRoots];
        }

        return [.. configured.Select(r => ResolveContentRoot(r, environment))];
    }

    /// <summary>
    ///     The config-owned half of a chartered agent: identity, tools, output cap and memory. Everything else
    ///     (<see cref="AgentEnvelope"/> has no <c>Description</c>/<c>Instructions</c>/<c>Model</c>/<c>Skills</c>)
    ///     comes from the role's active charter instead — see <c>CharteredAgentCatalog.Compose</c>.
    /// </summary>
    private static AgentEnvelope ToEnvelope(AgentConfig agent) => new()
    {
        Id = ParseAgentId(agent.Id, agent.Name),
        Name = agent.Name,
        Tools = [.. agent.Tools],
        MaxOutputTokens = agent.MaxOutputTokens,
        Memory = agent.Memory is null ? null : new AgentMemorySettings { Enabled = agent.Memory.Enabled, TopK = agent.Memory.TopK },
    };

    /// <summary>
    ///     Fails fast on the Daedalus-only <c>Thalos:Memory</c> keys (Thalos validates its own <c>MemoryOptions</c> on start).
    ///     A bad value here would otherwise surface much later as a rejected memory or a mis-sized vector column.
    /// </summary>
    private static void ValidateMemoryConfig(MemoryConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SharedOwnerId))
        {
            throw new InvalidOperationException($"{MemoryConfig.SectionName}:SharedOwnerId must not be blank.");
        }

        if (config.VectorDimensions <= 0)
        {
            throw new InvalidOperationException(
                $"{MemoryConfig.SectionName}:VectorDimensions must be greater than 0, but was {config.VectorDimensions}.");
        }

        if (config.RalphRecall.TopK < RalphRecallConfiguration.MinTopK)
        {
            throw new InvalidOperationException(
                $"{RalphRecallConfiguration.SectionName}:TopK must be at least {RalphRecallConfiguration.MinTopK}, but was {config.RalphRecall.TopK}.");
        }

        if (config.RalphRecall.MinScore is < 0 or > 1 || double.IsNaN(config.RalphRecall.MinScore))
        {
            throw new InvalidOperationException(
                $"{RalphRecallConfiguration.SectionName}:MinScore must be in [0, 1], but was {config.RalphRecall.MinScore.ToString(CultureInfo.InvariantCulture)}.");
        }

        ValidateInterval(config.Reindex.StartupDelay, nameof(ReindexConfig.StartupDelay));
        ValidateInterval(config.Reindex.RetryInterval, nameof(ReindexConfig.RetryInterval));
        ValidateInterval(config.Reindex.SweepInterval, nameof(ReindexConfig.SweepInterval));

        static void ValidateInterval(TimeSpan value, string key)
        {
            if (value <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"{MemoryConfig.SectionName}:Reindex:{key} must be greater than zero, but was {value.ToString(null, CultureInfo.InvariantCulture)}.");
            }
        }
    }

    /// <summary>
    ///     Refuses a second memory registration. <c>UseRagNetMemory</c> is last-call-wins, so calling
    ///     <see cref="AddDaedalusAgents(IServiceCollection, IConfiguration, IHostEnvironment, IEmbeddingGenerator{string, Embedding{float}}?)"/> and <see cref="AddDaedalusMemory"/> on one host would silently leave
    ///     <c>EnsureSchemaOnStartup</c> at whatever the later call passed — on the API host that means nobody creates
    ///     <c>rag_chunks</c> and every memory stays <c>index_pending</c> with nothing failing. Fail loudly instead.
    /// </summary>
    private static void ThrowIfMemoryAlreadyRegistered(IServiceCollection services, string method)
    {
        if (services.Any(d => d.ServiceType == typeof(MemoryConfig)))
        {
            throw new InvalidOperationException(
                $"{method} was called on a service collection that already registers Daedalus memory. " +
                $"{nameof(AddDaedalusAgents)} and {nameof(AddDaedalusMemory)} are mutually exclusive: the API host calls " +
                $"{nameof(AddDaedalusAgents)} (which owns the Rag.NET schema), every other host calls {nameof(AddDaedalusMemory)}.");
        }
    }

    /// <summary>
    ///     Registers skills on the Thalos builder: <c>Thalos:Skills</c> → <c>SkillOptions</c> with the roots already
    ///     resolved to absolute paths, plus the Postgres-backed store. The sync runs once at host start; a malformed
    ///     document is logged and skipped, but an unreachable store fails start (an agent missing its procedures is
    ///     worse than a host that does not come up).
    /// </summary>
    private static void ConfigureSkills(
        ThalosBuilder thalos,
        IConfigurationSection section,
        SkillsConfig config,
        IReadOnlyList<string> resolvedRoots)
    {
        thalos.UseSkills(o =>
            {
                section.Bind(o); // Enabled, Catalogue:MaxChars, Search:TopK/MinScore straight from Thalos:Skills
                o.Enabled = config.Enabled;

                // Absolute, resolved against the content root: no CWD surprises in tests or containers. Assigned
                // rather than cleared-and-filled because SkillOptions.Roots is settable and Bind appends to it.
                o.Roots = [.. resolvedRoots];
            })
            .UseSkillStore<PostgresSkillStore>();
    }

    /// <summary>
    ///     Registers the memory triple on the Thalos builder: <c>Thalos:Memory</c> → <c>MemoryOptions</c>, the Postgres store
    ///     and the Rag.NET index on the application database. <paramref name="ensureSchema"/> decides whether this host
    ///     creates the Rag.NET schema on start — exactly one host may, see the remarks on <see cref="AddDaedalusMemory"/>.
    /// </summary>
    private static void ConfigureMemory(
        ThalosBuilder thalos,
        IConfigurationSection section,
        MemoryConfig config,
        string connectionString,
        bool ensureSchema)
    {
        thalos.UseMemory(o =>
            {
                section.Bind(o); // Enabled, SharedOwnerId, Recall, Dedupe, ExposeTools straight from Thalos:Memory
                o.Enabled = config.Enabled;
                o.SharedOwnerId ??= config.SharedOwnerId;
            })
            .UseMemoryStore<PostgresMemoryStore>()
            .UseRagNetMemory(o =>
            {
                o.ConnectionString = connectionString; // same database as the app; Rag.NET keeps its own pool
                o.VectorDimensions = config.VectorDimensions;
                o.EnsureSchemaOnStartup = ensureSchema;
            });
    }

    /// <summary>
    ///     Fails fast on the Daedalus-only <c>Thalos:Skills</c> keys (Thalos validates its own <c>SkillOptions</c> on
    ///     start). A configured root that does not exist is fatal on purpose: an agent that silently lost every
    ///     procedure is indistinguishable from a healthy one, and this is what catches a broken content copy.
    /// </summary>
    private static void ValidateSkillsConfig(SkillsConfig config, IReadOnlyList<string> resolvedRoots)
    {
        if (!config.Enabled)
        {
            return;
        }

        for (var i = 0; i < config.Roots.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(config.Roots[i]))
            {
                throw new InvalidOperationException($"{SkillsConfig.SectionName}:Roots[{i}] must not be blank.");
            }
        }

        var missingRoot = resolvedRoots.FirstOrDefault(root => !Directory.Exists(root));
        if (missingRoot is not null)
        {
            throw new InvalidOperationException(
                $"{SkillsConfig.SectionName}:Roots contains '{missingRoot}', which is not an existing directory. " +
                "Relative roots resolve against the host content root; check that the skills folder is copied next to the host.");
        }

        if (config.Catalogue.MaxChars <= 0)
        {
            throw new InvalidOperationException(
                $"{SkillsConfig.SectionName}:Catalogue:MaxChars must be greater than 0, but was {config.Catalogue.MaxChars}.");
        }

        if (config.Search.TopK < 1)
        {
            throw new InvalidOperationException(
                $"{SkillsConfig.SectionName}:Search:TopK must be at least 1, but was {config.Search.TopK}.");
        }

        if (config.Search.MinScore is < 0 or > 1 || double.IsNaN(config.Search.MinScore))
        {
            throw new InvalidOperationException(
                $"{SkillsConfig.SectionName}:Search:MinScore must be in [0, 1], but was {config.Search.MinScore.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    /// <summary>
    ///     Resolves configured skill roots against the content root, like <c>ResolveMcpConfigPath</c>, falling back to the
    ///     assembly directory when that does not exist.
    /// </summary>
    /// <remarks>
    ///     The fallback is load-bearing, not defensive. <c>.mcp.json</c> is a file that physically lives in
    ///     <c>src/Daedalus.Api</c> <b>and</b> is copied to the output, so content-root resolution finds it either way.
    ///     Skills are different: they are authored at the repo root and only ever <i>copied</i> to the output directory.
    ///     In a published app the content root <i>is</i> the output directory, so <c>"skills"</c> resolves; but under
    ///     <c>dotnet run</c> (and therefore under Aspire) the content root is the <i>project</i> directory, where no
    ///     <c>skills</c> folder exists — so resolving against the content root alone kills every development host at
    ///     startup while tests and the container image stay green. What is invariably true in both layouts is that the
    ///     <c>Content</c> item puts the folder next to the assembly, which is what <see cref="AppContext.BaseDirectory"/>
    ///     names. An absolute root is taken as given, and when neither candidate exists the content-root path is kept so
    ///     the validation message names the location an operator would expect.
    /// </remarks>
    private static IReadOnlyList<string> ResolveSkillRoots(SkillsConfig config, IHostEnvironment environment) =>
        [.. config.Roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => ResolveContentRoot(r, environment))];

    /// <summary>
    ///     Resolves a single configured, repo-root-authored content folder — reused for
    ///     <c>Thalos:Skills:Roots</c> (via <see cref="ResolveSkillRoots"/>) and for
    ///     <c>Thalos:Workflow:ProcessesRoot</c>: both are directories of flat files checked into git next to the
    ///     source, not generated, so both need the same content-root/assembly-directory fallback. See
    ///     <see cref="ResolveSkillRoots"/>'s remarks for why the fallback is load-bearing rather than defensive.
    /// </summary>
    private static string ResolveContentRoot(string configured, IHostEnvironment environment)
    {
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        var fromContentRoot = Path.Combine(environment.ContentRootPath, configured);
        if (Directory.Exists(fromContentRoot))
        {
            return fromContentRoot;
        }

        var fromAssembly = Path.Combine(AppContext.BaseDirectory, configured);
        return Directory.Exists(fromAssembly) ? fromAssembly : fromContentRoot;
    }

    private static string ResolveMcpConfigPath(string configured, IHostEnvironment environment) =>
        Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);

    /// <summary>
    ///     Validates <c>Thalos:Workflow:StandingInstructionsPath</c> and returns it in canonical worktree-relative form:
    ///     forward-slash separated, with no <c>.</c> segment and no empty segment. That one form is what
    ///     <see cref="Workflow.ManufactureRunStarter"/> reads the run's pinned text from,
    ///     <see cref="Workflow.StandingInstructionsWriter"/> writes an approved proposal to, both through
    ///     <see cref="WorkspacePath.Resolve"/> inside the run's worktree, and the <c>workspace__*</c> tools protect in
    ///     <see cref="RunWorkspaceToolOptions.ProtectedPaths"/>, which they compare exactly, ignoring case only. So
    ///     <c>./AGENT.md</c> is protected as the <c>AGENT.md</c> a run would write.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     The file is overwritten with model-authored text that a human approved at the gate, so the check fails the
    ///     host at registration, rather than at every run, for a value that is blank; is
    ///     rooted, or carries a <c>:</c>, which on Linux is how a Windows-rooted <c>C:/x/AGENT.md</c> reads; climbs with
    ///     a <c>..</c> segment; is not a <c>.md</c> file; or is refused by <see cref="WorkspacePath.Resolve"/> itself. A
    ///     rooted value used to be accepted when it lay under the content root, and after the move into the worktree
    ///     <c>Path.Combine(worktree, rooted)</c> would have read and written that host file instead.
    ///     </para>
    ///     <para>
    ///     <b>Resolve is asked, not restated.</b> The canonical value is resolved against a fresh, empty stand-in
    ///     directory, so every rule <see cref="WorkspacePath.Resolve"/> applies to the value alone — the <c>.git</c>
    ///     directory and its <c>git~N</c> alias, a NUL character, and on Windows a reserved device name such as <c>CON</c> or a segment
    ///     ending in a dot or space — refuses the boot rather than every start, and cannot drift from the rule the
    ///     reader and writer meet at run time. The canonical form is built lexically, not with
    ///     <c>Path.GetFullPath</c>, which on Windows trims a trailing dot and would hide <c>docs/.../AGENT.md</c> from
    ///     that check, and which throws <see cref="ArgumentException"/> on a NUL character.
    ///     <see cref="WorkspacePath.Resolve"/> applies these rules again, and its link checks, at every
    ///     read and write, so a value that passes here still cannot leave the worktree through a link inside it.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The configured value breaks one of the rules above.</exception>
    internal static string StandingInstructionsRelativePath(string configured)
    {
        const string Key = WorkflowConfig.SectionName + ":StandingInstructionsPath";
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException($"{Key} is blank. It must name a .md file relative to the repository root.");
        }

        if (Path.IsPathRooted(configured) || configured.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{Key} is '{configured}', which is not a relative path. The standing-instructions file is read from and " +
                "written to each run's worktree, so the path must be relative to the repository root.");
        }

        var segments = configured.Split(['/', '\\']);
        if (segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"{Key} is '{configured}', which climbs with a '..' segment. The standing-instructions file is overwritten " +
                "with approved model text, so it must stay inside the run's worktree.");
        }

        // Lexical on purpose: only empty and '.' segments are dropped, so a segment Resolve refuses reaches it as written.
        var canonical = string.Join(
            '/', segments.Where(segment => segment.Length > 0 && !string.Equals(segment, ".", StringComparison.Ordinal)));
        if (!string.Equals(Path.GetExtension(canonical), ".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{Key} is '{configured}', which is not a .md file. The standing-instructions file is overwritten with " +
                "approved model text, so it must be a markdown file and never a configuration or code file.");
        }

        // The path is also protected, as a file and as a directory, in both modes, so the protected-path set is asked
        // first: it refuses a segment of only dots and spaces on every OS, while Resolve refuses it on Windows only, so
        // asking it before Resolve gives every OS the same refusal for the same rule.
        try
        {
            _ = new ProtectedPathSet([canonical, canonical + "/"]);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"{Key} is '{configured}', which cannot be a protected path: {ex.Message} The standing-instructions file " +
                "is protected from every run's writes, so a path that cannot be protected is refused.", ex);
        }

        var standIn = Directory.CreateTempSubdirectory("daedalus-standing-instructions-check-");
        try
        {
            var resolved = WorkspacePath.Resolve(standIn.FullName, canonical);
            if (resolved.IsFailure)
            {
                throw new InvalidOperationException(
                    $"{Key} is '{configured}', which a run's worktree does not permit: {resolved.Error.Message} Every run " +
                    "reads and writes the standing-instructions file there, so every run would fail.");
            }
        }
        finally
        {
            standIn.Delete(recursive: true);
        }

        return canonical;
    }

    private static AgentDefinition ToDefinition(AgentConfig agent) => new()
    {
        Id = ParseAgentId(agent.Id, agent.Name),
        Name = agent.Name,
        Description = agent.Description,
        Instructions = agent.Instructions,
        Model = agent.Model,
        MaxOutputTokens = agent.MaxOutputTokens,
        Tools = agent.Tools.Count == 0 ? ["*"] : [.. agent.Tools],
        Skills = [.. agent.Skills],
        Memory = agent.Memory is null ? null : new AgentMemorySettings { Enabled = agent.Memory.Enabled, TopK = agent.Memory.TopK },
    };

    private static AgentId ParseAgentId(string raw, string name)
    {
        if (AgentId.TryParse(raw, null, out var id))
        {
            return id;
        }

        if (Guid.TryParse(raw, out var guid))
        {
            return new AgentId(guid);
        }

        throw new InvalidOperationException($"Agent '{name}': Id '{raw}' is not a ULID or GUID (Thalos:Agents:*:Id).");
    }

    private static void ConfigureSentinel(SentinelOptions sentinel, SentinelConfig config, IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator)
    {
        sentinel.OnCritical = ParseAction(config.OnCritical, nameof(config.OnCritical));
        sentinel.OnHigh = ParseAction(config.OnHigh, nameof(config.OnHigh));
        sentinel.OnMedium = ParseAction(config.OnMedium, nameof(config.OnMedium));
        sentinel.OnLow = ParseAction(config.OnLow, nameof(config.OnLow));

        // Null keeps Sentinel lexical-only (it warns per pipeline); the API host passes the Ollama generator when configured.
        if (embeddingGenerator is not null)
        {
            sentinel.EmbeddingGenerator = embeddingGenerator;
        }

        foreach (var detectorName in config.DisabledDetectors)
        {
            DisableDetector(sentinel, detectorName);
        }
    }

    private static SentinelAction ParseAction(string value, string key) =>
        Enum.TryParse<SentinelAction>(value, ignoreCase: true, out var action)
            ? action
            : throw new InvalidOperationException(
                $"Thalos:Sentinel:{key} '{value}' is not a Sentinel action ({string.Join(", ", Enum.GetNames<SentinelAction>())}).");

    // Sentinel's per-detector configuration is generic over the detector type (Configure<TDetector>), while configuration can only
    // name detectors; resolve the type from AI.Sentinel's assembly and close the generic once at startup.
    private static void DisableDetector(SentinelOptions sentinel, string detectorName)
    {
        var detectorType = typeof(SentinelOptions).Assembly.GetExportedTypes()
            .FirstOrDefault(t => typeof(IDetector).IsAssignableFrom(t)
                                 && t is { IsAbstract: false, IsInterface: false }
                                 && string.Equals(t.Name, detectorName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Thalos:Sentinel:DisabledDetectors: '{detectorName}' is not an AI.Sentinel detector type name.");

        var configure = typeof(SentinelOptionsConfigureExtensions)
            .GetMethod(nameof(SentinelOptionsConfigureExtensions.Configure), BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("AI.Sentinel's SentinelOptionsConfigureExtensions.Configure<T> was not found.");

        configure.MakeGenericMethod(detectorType).Invoke(null, [sentinel, (Action<DetectorConfiguration>)(c => c.Enabled = false)]);
    }
}
