using System.Globalization;
using System.Reflection;
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
using Thalos.Git;
using Thalos.Git.LibGit2Sharp;
using Thalos.Mcp;
using Thalos.Memory;
using Thalos.Memory.RagNet;
using Thalos.Sentinel;
using Thalos.Skills;
using Thalos.Skills.Charters;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Daedalus.Agents;

/// <summary>Composition root for the Thalos-based agent stack. Ralph Loop registrations are untouched (strangler).</summary>
public static class DaedalusAgentsServiceCollectionExtensions
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

        AddGitHub(services, configuration);

        // Thalos's pull-request publisher abstraction, implemented over Daedalus's existing pull-request-factory
        // dispatcher, so the future git pull-request tool inherits GitHub and Azure DevOps routing for free. The
        // factory it delegates to is registered by AddCodeAnalysisServices in Daedalus.Infrastructure, not here — a
        // host calling this method alone resolves the publisher fine but only fails, at first use, if it never
        // called that one too. That mirrors how the factory itself already gets consumed across composition roots.
        services.AddScoped<IPullRequestPublisher, ThalosPullRequestPublisher>();

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
        var standingInstructionsPath = ResolveStandingInstructionsPath(options.Workflow.StandingInstructionsPath, environment);
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
            }

            thalos.UseAnthropic(configuration)
                .UseSessionStore<PostgresAgentSessionStore>()
                // Reads join the existing source the scout already allows; writes go in their own, which its
                // daedalus__* glob cannot name. See RepoActionToolSourceName for why the split is not the boundary.
                // DaedalusReviewTools joins this source rather than getting its own: report_review_outcome is a
                // pure validator with no side effect on anything outside its own turn, so it needs no separate
                // write boundary, and the reviewer's daedalus__* grant already names it.
                .AddLocalTools(KnowledgeToolSourceName, typeof(DaedalusKnowledgeTools), typeof(DaedalusScheduleTools), typeof(DaedalusRepoTools), typeof(DaedalusReviewTools))
                .AddLocalTools(RepoActionToolSourceName, typeof(DaedalusRepoActionTools))
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
                .AddMcpServersFromFile(ResolveMcpConfigPath(options.McpConfigPath, environment))
                .AddPolicy<DeveloperPolicy>();

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
            AddDaedalusWorkflow(services, processesRoot, standingInstructionsPath);
        }

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
    private static void AddDaedalusWorkflow(IServiceCollection services, string processesRoot, string standingInstructionsPath)
    {
        // IAgentCatalog and ISkillStore both come from AddThalos above. Thalos' own resolver is wrapped in
        // SquadWorkflowReferenceResolver so a process file's `agent:` name goes through SquadAgentResolver
        // first - which is what makes Thalos:Squad:Enabled=false actually collapse implementer and reviewer
        // onto the fallback agent. Wrapped here, at the single registration, rather than inside
        // WorkflowNodeDispatcherFactory: this resolver is deliberately the one lookup shared by
        // ProcessValidator at load time and WorkflowNodeDispatcher at dispatch time, and decorating only one
        // of them would let a process validate against one set of agent names and then run against another.
        services.AddSingleton<IWorkflowReferenceResolver>(sp => new SquadWorkflowReferenceResolver(
            new WorkflowReferenceResolver(sp.GetRequiredService<IAgentCatalog>(), sp.GetRequiredService<ISkillStore>()),
            sp.GetRequiredService<SquadAgentResolver>(),
            sp.GetRequiredService<ILogger<SquadWorkflowReferenceResolver>>()));

        // Where a workflow turn's recall tier is parked between the recall that produced it and the transition
        // that records it. Registered here rather than beside SquadAgentResolver because nothing outside a
        // workflow run ever writes to it: RecallTierRecordingMemoryService only records for a WorkflowCaller.
        services.AddSingleton<WorkflowRecallTierLog>();
        DecorateMemoryServiceWithRecallTierRecording(services);

        // Task B5: the one type that ever writes Thalos:Workflow:StandingInstructionsPath, and only from
        // WorkflowRunGateway's five-argument ResumeAsync overload, on a human's explicit applyStandingInstructions.
        // Registered here, workflow-enabled hosts only, alongside the gateway it is injected into below.
        services.AddSingleton<StandingInstructionsWriter>();

        // The resume/cancel REST boundary's only path to IWorkflowStore (from AddWorkflowOrm above). Never an
        // agent, never a Thalos tool — see WorkflowRunGateway's own remarks for why, and for what actually
        // authorizes a call to it (ASP.NET Core's WorkflowResume policy in Daedalus.Api/Program.cs, not
        // Thalos:ToolPolicies, which DefaultToolAuthorizer only ever evaluates against a tool call).
        services.AddSingleton<WorkflowRunGateway>();

        // Replaces the DisabledManufactureRunStarter registered unconditionally above, now that WorkflowRunStarter
        // (from AddWorkflowOrm, inside AddThalos) and IWorkflowReferenceResolver (just above) both resolve.
        // Replace, not TryAdd: TryAddSingleton is first-registration-wins, and the disabled default was already
        // added before this method ever runs.
        services.Replace(ServiceDescriptor.Singleton<IManufactureRunStarter>(sp =>
            new ManufactureRunStarter(sp.GetRequiredService<WorkflowRunStarter>(), standingInstructionsPath)));

        // ISubagentRunner comes from AddThalos; IWorkflowStore/IProcessDefinitionStore from AddWorkflowOrm above.
        // Built via WorkflowNodeDispatcherFactory, not inline here — see that type's remarks for why this needs
        // the raw ISubagentRunner instead of ISubagentRunExecutor, and why isolating the call there keeps this
        // composition-root class off CleanArchitectureTests' single-seam rule.
        services.AddSingleton(WorkflowNodeDispatcherFactory.Create);

        // The only part of definition handling that is host policy — see FileSystemProcessDefinitionSource's remarks.
        services.AddSingleton<IProcessDefinitionSource>(_ => new FileSystemProcessDefinitionSource(processesRoot));
        services.AddSingleton<ProcessDefinitionSync>();
        services.AddHostedService<ProcessDefinitionSyncHostedService>();

        // The outbox consumer: see WorkflowOutboxDispatchService's remarks for why this is a hand-rolled poller
        // rather than a second ZeroAlloc.Outbox AddOutbox() call.
        services.AddSingleton<WorkflowDispatchOutboxDispatcher>();
        services.AddSingleton<WorkflowOutboxDispatchOptions>();
        services.AddHostedService<WorkflowOutboxDispatchService>();

        // The stranded-run sweep: Thalos ships WorkflowRunReconciler with no timer of its own, deliberately.
        services.AddSingleton<WorkflowRunReconciler>();
        services.AddHostedService<WorkflowStrandedRunSweepService>();
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

        static object CreateInner(IServiceProvider sp, ServiceDescriptor descriptor) =>
            descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
    }

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
    ///     Resolves <c>Thalos:Workflow:StandingInstructionsPath</c> against the content root, the same rule
    ///     <see cref="ResolveMcpConfigPath"/> applies to <c>Thalos:McpConfigPath</c> — a single file, not a
    ///     directory, so unlike <see cref="ResolveContentRoot"/> there is no assembly-directory fallback to fall
    ///     back to: a missing file is not this method's problem to solve, see
    ///     <see cref="Workflow.ManufactureRunStarter.StartAsync"/>.
    /// </summary>
    /// <remarks>
    ///     <c>internal</c>, not <c>private</c>: <see cref="Workflow.StandingInstructionsWriter"/> reuses this same
    ///     resolution rule for its own path rather than restating it, per task B5. Two independently-maintained
    ///     copies of "how a configured, possibly-relative path resolves against the content root" is exactly the
    ///     kind of drift this method's own doc comment already warns against for <see cref="ResolveContentRoot"/>.
    /// </remarks>
    internal static string ResolveStandingInstructionsPath(string configured, IHostEnvironment environment) =>
        Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);

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
