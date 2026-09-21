using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Daedalus.Agents;
using Daedalus.Agents.Scheduling;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Persistence;
using Rag.NET.Abstractions;
using Rag.NET.PgVector;
using Thalos;
using Thalos.Anthropic;
using Thalos.Channels;
using Thalos.Channels.Telegram;
using Thalos.Mcp;
using Thalos.Memory;
using Thalos.Memory.RagNet;
using Thalos.Sentinel;
using Thalos.Skills;
using ZeroAlloc.Outbox;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using SysAssembly = System.Reflection.Assembly;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Tests.Unit.Architecture;

/// <summary>
///     Enforces Clean Architecture layer dependency rules using ArchUnitNET.
///     Validates that lower layers do not depend on higher layers and that
///     naming conventions are respected across the codebase.
///     Uses assembly-based layer matching for accuracy across all sub-namespaces.
/// </summary>
public sealed class CleanArchitectureTests
{
    /// <summary>Anchored: <c>Thalos</c> and <c>Thalos.*</c>, but not e.g. <c>ThalosSomethingElse</c>.</summary>
    private const string ThalosNamespacePattern = "^Thalos(\\.|$)";

    /// <summary>
    ///     Anchored: <c>Rag</c> and <c>Rag.*</c>, but not e.g. <c>Ragged</c>. Rag.NET's root namespace segment is
    ///     <c>Rag</c> (full namespaces are <c>Rag.NET</c>, <c>Rag.NET.Abstractions</c>, <c>Rag.NET.Models</c>,
    ///     <c>Rag.NET.VectorStores.PgVector</c>, …), so anchoring on <c>Rag</c> covers the whole product.
    /// </summary>
    private const string RagNetNamespacePattern = "^Rag(\\.|$)";

    /// <summary>Anchored on the skills package's own namespace, so the fact fails if the assembly is not loaded.</summary>
    private const string SkillsNamespacePattern = "^Thalos\\.Skills(\\.|$)";

    /// <summary>
    ///     Anchored on the channels package's own namespace. Covers both <c>Thalos.Channels</c> and the nested
    ///     <c>Thalos.Channels.Telegram</c> (the Telegram adapter's namespace sits under the channels namespace), so
    ///     the fact fails if either assembly is not loaded.
    /// </summary>
    private const string ChannelsNamespacePattern = "^Thalos\\.Channels(\\.|$)";

    /// <summary>Anchored on EF Core's own namespace, so the fact fails if the assembly is not loaded.</summary>
    private const string EfCoreNamespacePattern = "^Microsoft\\.EntityFrameworkCore(\\.|$)";

    /// <summary>
    ///     Anchored on ZeroAlloc.Outbox's own namespace, so the fact fails if the assembly is not loaded. Unlike
    ///     <c>ZeroAlloc.Saga</c> and <c>ZeroAlloc.Scheduling</c> (see <see cref="No_project_references_ZeroAlloc_Saga"/>
    ///     and <see cref="No_project_references_ZeroAlloc_Scheduling"/>), this package genuinely is referenced
    ///     elsewhere in the solution (<c>Daedalus.Agents</c>, <c>Daedalus.Infrastructure</c>), so a namespace rule
    ///     over it is capable of failing and is not the vacuous-rule trap those two packages fall into.
    /// </summary>
    private const string ZeroAllocOutboxNamespacePattern = "^ZeroAlloc\\.Outbox(\\.|$)";

    /// <summary>
    ///     Anchored on Cronos' own namespace, so the fact fails if the assembly is not loaded. Cronos is genuinely
    ///     referenced by <c>Daedalus.Agents</c> (<see cref="ScheduleReconciler"/> and
    ///     <see cref="ScheduledRunStore"/>), so this rule is non-vacuous the same way the
    ///     <see cref="ZeroAllocOutboxNamespacePattern"/> rule is.
    /// </summary>
    private const string CronosNamespacePattern = "^Cronos(\\.|$)";

    private static readonly SysAssembly DomainAssembly = typeof(Task).Assembly;
    private static readonly SysAssembly ApplicationAssembly = typeof(ITaskRepository).Assembly;
    private static readonly SysAssembly InfrastructureAssembly = typeof(ApplicationDbContext).Assembly;
    private static readonly SysAssembly ApiAssembly = typeof(Api.Program).Assembly;
    private static readonly SysAssembly AgentsAssembly = typeof(DaedalusAgentsServiceCollectionExtensions).Assembly;
    private static readonly SysAssembly WebAssembly = typeof(Daedalus.Web.App).Assembly;

    /// <summary>The Ralph console host. Has a public type (<c>RalphLoopWorker</c>) to anchor a <c>typeof()</c> on.</summary>
    private static readonly SysAssembly ConsoleAssembly = typeof(Daedalus.Console.RalphLoopWorker).Assembly;

    /// <summary>
    ///     The CLI host. Loaded by simple name, not <c>typeof()</c>: <c>Daedalus.Cli</c> has exactly one source
    ///     file (<c>Program.cs</c>, top-level statements) and declares no public type, only the <c>internal</c>
    ///     <c>CliHostServices</c> — and its <c>InternalsVisibleTo</c> grants only <c>Daedalus.Tests.Integration</c>,
    ///     not this project. <c>Daedalus.Tests.Unit.csproj</c> carries a <c>ProjectReference</c> to
    ///     <c>Daedalus.Cli</c> purely so its assembly is present in this project's output directory for this call
    ///     to find.
    /// </summary>
    private static readonly SysAssembly CliAssembly = SysAssembly.Load("Daedalus.Cli");

    private static readonly SysAssembly MemoryRagNetAssembly = typeof(RagNetMemoryOptions).Assembly; // Thalos.NET.Memory.RagNet

    /// <summary>Microsoft.EntityFrameworkCore itself, loaded so <see cref="DomainLayer_ShouldNotDependOn_EfCore"/> is non-vacuous.</summary>
    private static readonly SysAssembly EfCoreAssembly = typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly; // Microsoft.EntityFrameworkCore

    /// <summary>ZeroAlloc.Outbox itself, loaded so <see cref="DomainLayer_ShouldNotDependOn_ZeroAllocOutbox"/> is non-vacuous.</summary>
    private static readonly SysAssembly ZeroAllocOutboxAssembly = typeof(IOutboxWriter<>).Assembly; // ZeroAlloc.Outbox

    /// <summary>Cronos itself, loaded so <see cref="DomainLayer_ShouldNotDependOn_Cronos"/> is non-vacuous.</summary>
    private static readonly SysAssembly CronosAssembly = typeof(Cronos.CronExpression).Assembly; // Cronos

    private static readonly SysAssembly[] ThalosAssemblies =
    [
        typeof(IAgentRuntime).Assembly, // Thalos.NET.Abstractions
        typeof(ThalosBuilder).Assembly, // Thalos.NET
        typeof(AnthropicThalosBuilderExtensions).Assembly, // Thalos.NET.Anthropic
        typeof(IConversationMap).Assembly, // Thalos.NET.Channels
        typeof(TelegramThalosBuilderExtensions).Assembly, // Thalos.NET.Channels.Telegram
        typeof(McpThalosBuilderExtensions).Assembly, // Thalos.NET.Mcp
        typeof(SentinelThalosBuilderExtensions).Assembly, // Thalos.NET.Sentinel
        typeof(IMemoryService).Assembly, // Thalos.NET.Memory
        MemoryRagNetAssembly, // Thalos.NET.Memory.RagNet
        typeof(ISkillStore).Assembly, // Thalos.NET.Skills
    ];

    /// <summary>
    ///     Rag.NET reaches the solution only transitively (Thalos.NET.Memory.RagNet). ArchUnitNET does not synthesise
    ///     types for assemblies it has not loaded, so without these the <c>^Rag(\.|$)</c> rules would pass vacuously —
    ///     <see cref="MemoryRagNetAdapter_DoesDependOn_RagNet_RuleIsLoadBearing"/> guards exactly that.
    /// </summary>
    private static readonly SysAssembly[] RagNetAssemblies =
    [
        typeof(IVectorStore).Assembly, // Rag.NET.Abstractions
        typeof(PgVectorStore).Assembly, // Rag.NET.VectorStores.PgVector
    ];

    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(DomainAssembly, ApplicationAssembly, InfrastructureAssembly, ApiAssembly, AgentsAssembly, WebAssembly)
        .LoadAssemblies(ConsoleAssembly, CliAssembly)
        .LoadAssemblies(ThalosAssemblies)
        .LoadAssemblies(RagNetAssemblies)
        .LoadAssemblies(EfCoreAssembly)
        .LoadAssemblies(ZeroAllocOutboxAssembly, CronosAssembly)
        .Build();

    private static readonly IObjectProvider<IType> DomainTypes =
        Types().That().ResideInAssembly(DomainAssembly)
            .As("Domain Layer");

    private static readonly IObjectProvider<IType> ApplicationTypes =
        Types().That().ResideInAssembly(ApplicationAssembly)
            .As("Application Layer");

    private static readonly IObjectProvider<IType> InfrastructureTypes =
        Types().That().ResideInAssembly(InfrastructureAssembly)
            .As("Infrastructure Layer");

    private static readonly IObjectProvider<IType> ApiTypes =
        Types().That().ResideInAssembly(ApiAssembly)
            .As("API Layer");

    private static readonly IObjectProvider<IType> AgentsTypes =
        Types().That().ResideInAssembly(AgentsAssembly)
            .As("Agents adapter (Daedalus.Agents)");

    private static readonly IObjectProvider<IType> WebTypes =
        Types().That().ResideInAssembly(WebAssembly)
            .As("Web (Blazor WASM)");

    private static readonly IObjectProvider<IType> ThalosTypes =
        Types().That().ResideInNamespaceMatching(ThalosNamespacePattern)
            .As("Thalos.NET");

    private static readonly IObjectProvider<IType> RagNetTypes =
        Types().That().ResideInNamespaceMatching(RagNetNamespacePattern)
            .As("Rag.NET");

    private static readonly IObjectProvider<IType> MemoryRagNetTypes =
        Types().That().ResideInAssembly(MemoryRagNetAssembly)
            .As("Thalos.NET.Memory.RagNet");

    /// <summary>
    ///     Thalos' <c>ISubagentRunner</c> — the single seam <see cref="OnlySubagentRunExecutor_DependsOn_ISubagentRunner"/>
    ///     restricts to <see cref="SubagentRunExecutor"/> alone. If Thalos ever renames or moves this type, this
    ///     matcher silently resolves to nothing and the rule above would pass without checking anything —
    ///     <see cref="SubagentRunnerType_IsLoaded_SoTheSingleSeamRuleCoversIt"/> guards exactly that, the same way
    ///     <see cref="EfCoreAssembly_IsLoaded_SoTheDomainRuleCoversIt"/> guards
    ///     <see cref="DomainLayer_ShouldNotDependOn_EfCore"/>.
    /// </summary>
    private static readonly IObjectProvider<IType> SubagentRunnerType =
        Types().That().HaveFullName("Thalos.ISubagentRunner")
            .As("ISubagentRunner (Thalos.NET)");

    /// <summary>
    ///     Every type declared in this solution's own assemblies, as opposed to a referenced package's. Used to
    ///     scope <see cref="OnlySubagentRunExecutor_DependsOn_ISubagentRunner"/> to Daedalus code: Thalos' own
    ///     <c>SubagentRunner</c> (the default <c>ISubagentRunner</c> implementation) and its own DI registration
    ///     both legitimately "depend on" the interface they define and wire up, and are not offenders. Covers
    ///     every host in the solution, including the thin entry points (<c>Daedalus.Console</c>,
    ///     <c>Daedalus.Cli</c>) — the rule claims the whole solution, so the scope must actually be the whole
    ///     solution, not just the layers with the most code in them.
    /// </summary>
    private static readonly IObjectProvider<IType> DaedalusOwnTypes =
        Types().That().ResideInAssembly(DomainAssembly)
            .Or().ResideInAssembly(ApplicationAssembly)
            .Or().ResideInAssembly(InfrastructureAssembly)
            .Or().ResideInAssembly(ApiAssembly)
            .Or().ResideInAssembly(AgentsAssembly)
            .Or().ResideInAssembly(WebAssembly)
            .Or().ResideInAssembly(ConsoleAssembly)
            .Or().ResideInAssembly(CliAssembly)
            .As("Daedalus (this solution's own assemblies)");

    [Fact]
    public void DomainLayer_ShouldNotDependOn_ApplicationLayer()
    {
        var rule = Types().That().Are(DomainTypes)
            .Should().NotDependOnAny(ApplicationTypes)
            .Because("Domain layer must be independent of Application layer");

        rule.Check(Architecture);
    }

    [Fact]
    public void DomainLayer_ShouldNotDependOn_InfrastructureLayer()
    {
        var rule = Types().That().Are(DomainTypes)
            .Should().NotDependOnAny(InfrastructureTypes)
            .Because("Domain layer must be independent of Infrastructure layer");

        rule.Check(Architecture);
    }

    [Fact]
    public void DomainLayer_ShouldNotDependOn_ApiLayer()
    {
        var rule = Types().That().Are(DomainTypes)
            .Should().NotDependOnAny(ApiTypes)
            .Because("Domain layer must be independent of API layer");

        rule.Check(Architecture);
    }

    [Fact]
    public void ApplicationLayer_ShouldNotDependOn_InfrastructureLayer()
    {
        var rule = Types().That().Are(ApplicationTypes)
            .Should().NotDependOnAny(InfrastructureTypes)
            .Because("Application layer should only depend on Domain, not Infrastructure");

        rule.Check(Architecture);
    }

    [Fact]
    public void ApplicationLayer_ShouldNotDependOn_ApiLayer()
    {
        var rule = Types().That().Are(ApplicationTypes)
            .Should().NotDependOnAny(ApiTypes)
            .Because("Application layer should not depend on API layer");

        rule.Check(Architecture);
    }

    [Fact]
    public void InfrastructureLayer_ShouldNotDependOn_ApiLayer()
    {
        var rule = Types().That().Are(InfrastructureTypes)
            .Should().NotDependOnAny(ApiTypes)
            .Because("Infrastructure layer should not depend on API layer");

        rule.Check(Architecture);
    }

    // ---- Thalos / Daedalus.Agents boundaries (strangler: Thalos lives only in Daedalus.Agents and the API) ----

    [Fact]
    public void DomainLayer_ShouldNotDependOn_Thalos()
    {
        var rule = Types().That().Are(DomainTypes)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(ThalosNamespacePattern)
            .Because("Domain must not know the agent framework");

        rule.Check(Architecture);
    }

    [Fact]
    public void DomainLayer_ShouldNotDependOn_EfCore()
    {
        // ChannelConversation (like every other Domain entity) must stay framework-free: the EF Core mapping for it
        // is ChannelConversationConfiguration, which lives in Daedalus.Infrastructure, never in Domain itself.
        var rule = Types().That().Are(DomainTypes)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(EfCoreNamespacePattern)
            .Because("Domain entities must stay persistence-ignorant; EF Core configuration lives in Infrastructure");

        rule.Check(Architecture);
    }

    [Fact]
    public void DomainLayer_ShouldNotDependOn_ZeroAllocOutbox()
    {
        // ZeroAlloc.Outbox's [OutboxMessage] records -- ScheduledRunDue, RunScoutStep, RunWriterStep, DeliverDigest
        // -- live in Daedalus.Agents, never in Domain. ScheduledRunExecution (Domain) only records which step a run
        // is at; it does not know how that step got dispatched.
        var rule = Types().That().Are(DomainTypes)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(ZeroAllocOutboxNamespacePattern)
            .Because("Domain must stay ignorant of the outbox transport; step messages live in Daedalus.Agents");

        rule.Check(Architecture);
    }

    [Fact]
    public void DomainLayer_ShouldNotDependOn_Cronos()
    {
        // ScheduledRun.Cron is stored as a plain string and parsed only by ScheduleReconciler and
        // ScheduledRunStore (both in Daedalus.Agents), precisely so Cronos -- a framework concern -- stays out of
        // Domain. See ScheduleReconciler's own remarks for why cron parsing lives there.
        var rule = Types().That().Are(DomainTypes)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(CronosNamespacePattern)
            .Because("Domain must not parse cron expressions; that framework concern lives in Daedalus.Agents");

        rule.Check(Architecture);
    }

    [Fact]
    public void OnlySubagentRunExecutor_DependsOn_ISubagentRunner()
    {
        // The single-seam claim (Task 14): every step dispatcher reaches an LLM through ISubagentRunExecutor,
        // never through Thalos' concrete ISubagentRunner directly. Enforced here rather than left as a comment.
        // Scoped to DaedalusOwnTypes: Thalos' own SubagentRunner (the default implementation) and its own DI
        // registration both legitimately depend on the interface they define, and are not offenders.
        var rule = Types().That().Are(DaedalusOwnTypes).And().DependOnAny(SubagentRunnerType)
            .And().DoNotHaveFullName(typeof(SubagentRunExecutor).FullName!)
            .Should().NotExist()
            .Because("SubagentRunExecutor is the only Daedalus type permitted to depend on Thalos' " +
                      "ISubagentRunner; every other caller must go through ISubagentRunExecutor instead");

        rule.Check(Architecture);
    }

    /// <summary>Known positive: proves <c>Thalos.ISubagentRunner</c> is loaded so the single-seam rule covers it.</summary>
    [Fact]
    public void SubagentRunnerType_IsLoaded_SoTheSingleSeamRuleCoversIt()
    {
        // Same trap as the other "loaded" facts in this file: if Thalos ever renames or moves ISubagentRunner,
        // HaveFullName("Thalos.ISubagentRunner") would resolve to nothing, DependOnAny would find an empty set,
        // and OnlySubagentRunExecutor_DependsOn_ISubagentRunner would pass forever while checking nothing.
        var rule = Types().That().Are(SubagentRunnerType)
            .Should().Exist()
            .Because("Thalos.ISubagentRunner must resolve to a real loaded type or the single-seam rule never sees it");

        rule.Check(Architecture);
    }

    [Fact]
    public void DomainLayer_ShouldNotDependOn_AgentsProject()
    {
        var rule = Types().That().Are(DomainTypes)
            .Should().NotDependOnAny(AgentsTypes)
            .Because("Daedalus.Agents is an adapter on top of Domain, never the other way round");

        rule.Check(Architecture);
    }

    [Fact]
    public void ApplicationLayer_ShouldNotDependOn_Thalos()
    {
        var rule = Types().That().Are(ApplicationTypes)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(ThalosNamespacePattern)
            .Because("Application DTOs are shared with the WASM client and must stay Thalos-free");

        rule.Check(Architecture);
    }

    [Fact]
    public void ApplicationLayer_ShouldNotDependOn_AgentsProject()
    {
        var rule = Types().That().Are(ApplicationTypes)
            .Should().NotDependOnAny(AgentsTypes)
            .Because("Application defines the DTOs the Agents adapter maps to, not vice versa");

        rule.Check(Architecture);
    }

    [Fact]
    public void AgentsProject_ShouldNotDependOn_ApiLayer()
    {
        var rule = Types().That().Are(AgentsTypes)
            .Should().NotDependOnAny(ApiTypes)
            .Because("The API composes Daedalus.Agents; the adapter must not reach back into controllers");

        rule.Check(Architecture);
    }

    [Fact]
    public void RalphCode_ShouldNotDependOn_Thalos_YetStranglerBoundary()
    {
        var rule = Types().That().Are(InfrastructureTypes)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(ThalosNamespacePattern)
            .Because("Ralph Loop (Infrastructure) is left untouched until the strangler migration replaces it");

        rule.Check(Architecture);
    }

    [Fact]
    public void WebLayer_ShouldNotDependOn_Thalos()
    {
        var rule = Types().That().Are(WebTypes)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(ThalosNamespacePattern)
            .Because("The WASM client talks to the agent API through Application DTOs only");

        rule.Check(Architecture);
    }

    [Fact]
    public void AgentsProject_DoesDependOn_Thalos_RulesAreLoadBearing()
    {
        // Known positive: if the Thalos namespace pattern stopped matching anything, the negative rules above would pass
        // vacuously. Daedalus.Agents is the one place that must reference Thalos.
        var rule = Types().That().Are(AgentsTypes).And().DependOnAny(ThalosTypes)
            .Should().Exist()
            .Because("Daedalus.Agents is the Thalos adapter; the pattern must resolve to real Thalos types");

        rule.Check(Architecture);
    }

    // ---- Rag.NET boundary: the memory index is an implementation detail of Daedalus.Agents ----

    [Fact]
    public void OnlyAgentsProject_DependsOn_RagNet()
    {
        // Rag.NET (Rag.NET.Abstractions + Rag.NET.VectorStores.PgVector) reaches Daedalus only transitively, through
        // Thalos.NET.Memory.RagNet, which only Daedalus.Agents references. The memory index is a rebuildable cache
        // behind Thalos' IMemoryIndex: no other layer may take a compile-time dependency on the vector store.
        (IObjectProvider<IType> Layer, string Name)[] layers =
        [
            (DomainTypes, "Domain"),
            (ApplicationTypes, "Application"),
            (InfrastructureTypes, "Infrastructure"),
            (ApiTypes, "API"),
            (WebTypes, "Web"),
        ];

        foreach (var (layer, name) in layers)
        {
            var rule = Types().That().Are(layer)
                .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(RagNetNamespacePattern)
                .Because($"{name} must not know the vector store; Rag.NET lives behind Thalos' IMemoryIndex in Daedalus.Agents");

            rule.Check(Architecture);
        }
    }

    [Fact]
    public void MemoryRagNetAdapter_DoesDependOn_RagNet_RuleIsLoadBearing()
    {
        // Known positive: nothing in this solution references Rag.NET directly, so the rule above would pass vacuously
        // if the pattern stopped matching (Rag.NET's root namespace segment is "Rag"). Thalos.NET.Memory.RagNet is the
        // adapter onto Rag.NET, so its types must resolve to real Rag.NET types in the loaded architecture.
        var rule = Types().That().Are(MemoryRagNetTypes).And().DependOnAny(RagNetTypes)
            .Should().Exist()
            .Because("Thalos.NET.Memory.RagNet is the Rag.NET adapter; the pattern must resolve to real Rag.NET types");

        rule.Check(Architecture);
    }

    [Fact]
    public void Controllers_ShouldResideIn_ApiLayer()
    {
        var rule = Classes().That().HaveNameEndingWith("Controller")
            .Should().ResideInAssembly(ApiAssembly)
            .Because("Controllers are API-layer concerns");

        rule.Check(Architecture);
    }

    [Fact]
    public void RepositoryInterfaces_ShouldResideIn_ApplicationLayer()
    {
        var rule = Interfaces().That().HaveNameStartingWith("I")
            .And().HaveNameEndingWith("Repository")
            .Should().ResideInAssembly(ApplicationAssembly)
            .Because("Repository contracts belong in Application layer");

        rule.Check(Architecture);
    }

    [Fact]
    public void RepositoryImplementations_ShouldResideIn_InfrastructureLayer()
    {
        var rule = Classes().That().HaveNameEndingWith("Repository")
            .And().AreNotAbstract()
            .Should().ResideInAssembly(InfrastructureAssembly)
            .Because("Repository implementations belong in Infrastructure layer");

        rule.Check(Architecture);
    }

    /// <summary>Known positive: proves Thalos.NET.Skills is loaded so the Thalos boundary rules can see it.</summary>
    [Fact]
    public void SkillsAssembly_IsLoaded_SoTheThalosRulesCoverIt()
    {
        // ArchUnitNET does not synthesise types for assemblies it has not loaded, so without Thalos.NET.Skills in
        // ThalosAssemblies every Thalos-namespace rule above would pass vacuously for skills types - the same trap
        // the Rag.NET rules hit in phase 1.2.
        var rule = Types().That().ResideInNamespaceMatching(SkillsNamespacePattern)
            .Should().Exist()
            .Because("Thalos.NET.Skills must be loaded into the architecture or the Thalos boundary rules never see a skills type");

        rule.Check(Architecture);
    }

    /// <summary>
    ///     Known positive: proves Thalos.NET.Channels and Thalos.NET.Channels.Telegram are loaded so the Thalos
    ///     boundary rules can see them.
    /// </summary>
    [Fact]
    public void ChannelsAssemblies_AreLoaded_SoTheThalosRulesCoverThem()
    {
        // Same trap as Skills (phase 1.3) and Rag.NET (phase 1.2): without Thalos.NET.Channels and
        // Thalos.NET.Channels.Telegram in ThalosAssemblies, every Thalos-namespace rule above (including
        // DomainLayer_ShouldNotDependOn_Thalos) would pass vacuously for channels types - it would look like the
        // layering held when it was never actually checked.
        var rule = Types().That().ResideInNamespaceMatching(ChannelsNamespacePattern)
            .Should().Exist()
            .Because("Thalos.NET.Channels and Thalos.NET.Channels.Telegram must be loaded into the architecture or " +
                     "the Thalos boundary rules never see a channels type");

        rule.Check(Architecture);
    }

    /// <summary>Known positive: proves Microsoft.EntityFrameworkCore is loaded so <see cref="DomainLayer_ShouldNotDependOn_EfCore"/> is non-vacuous.</summary>
    [Fact]
    public void EfCoreAssembly_IsLoaded_SoTheDomainRuleCoversIt()
    {
        // Same trap as above: without Microsoft.EntityFrameworkCore explicitly loaded, DomainLayer_ShouldNotDependOn_EfCore
        // would pass regardless of whether Domain actually referenced EF Core, because ArchUnitNET does not synthesise
        // types for assemblies it has not loaded.
        var rule = Types().That().ResideInNamespaceMatching(EfCoreNamespacePattern)
            .Should().Exist()
            .Because("Microsoft.EntityFrameworkCore must be loaded into the architecture or the Domain-EF-Core rule never sees an EF Core type");

        rule.Check(Architecture);
    }

    /// <summary>Known positive: proves ZeroAlloc.Outbox is loaded so <see cref="DomainLayer_ShouldNotDependOn_ZeroAllocOutbox"/> is non-vacuous.</summary>
    [Fact]
    public void ZeroAllocOutboxAssembly_IsLoaded_SoTheDomainRuleCoversIt()
    {
        var rule = Types().That().ResideInNamespaceMatching(ZeroAllocOutboxNamespacePattern)
            .Should().Exist()
            .Because("ZeroAlloc.Outbox must be loaded into the architecture or the Domain-outbox rule never sees an outbox type");

        rule.Check(Architecture);
    }

    /// <summary>Known positive: proves Cronos is loaded so <see cref="DomainLayer_ShouldNotDependOn_Cronos"/> is non-vacuous.</summary>
    [Fact]
    public void CronosAssembly_IsLoaded_SoTheDomainRuleCoversIt()
    {
        var rule = Types().That().ResideInNamespaceMatching(CronosNamespacePattern)
            .Should().Exist()
            .Because("Cronos must be loaded into the architecture or the Domain-Cronos rule never sees a Cronos type");

        rule.Check(Architecture);
    }

    // ---- Packages ArchUnitNET cannot see, because nothing in the solution loads them ----
    //
    // ArchUnitNET only synthesises types for assemblies it is explicitly told to load (see the "known positive"
    // facts above). A namespace rule against a package nobody references would never see a single type belonging
    // to it, so the rule would report success without ever having checked anything -- the exact trap
    // DomainLayer_ShouldNotDependOn_EfCore's own non-vacuity fact documents. ZeroAlloc.Saga and ZeroAlloc.Scheduling
    // are both in that position: the whole point of dropping them is that no project references them, so an
    // ArchUnitNET rule here would be vacuously true by construction. Asserting directly on .csproj text is the only
    // way to make "must not be referenced at all" an assertion that can actually fail.

    [Fact]
    public void No_project_references_ZeroAlloc_Saga()
    {
        var offenders = FindCsprojFilesReferencing("ZeroAlloc.Saga");

        offenders.Should().BeEmpty(
            "a saga never receives its trigger event: ZeroAlloc.Mediator's generated Publish dispatches to a " +
            "closed list of concrete handler types in its own compilation and never enumerates " +
            "INotificationHandler<T> from DI. See docs/plans/2026-09-16-saga-efcore-spike.md and ZeroAlloc.Saga " +
            "issue 127. Phase 1.5 removed the dependency; re-adding it compiles and then silently does nothing " +
            "at run time.");
    }

    [Fact]
    public void No_project_references_ZeroAlloc_Scheduling()
    {
        var offenders = FindCsprojFilesReferencing("ZeroAlloc.Scheduling");

        offenders.Should().BeEmpty(
            "ZeroAlloc.Scheduling was dropped for this phase: its EF Core job store requires a separate " +
            "SchedulingDbContext that ships no migrations and cannot be bootstrapped with EnsureCreated, and the " +
            "sweep this solution needs is idempotent, so durable job state buys nothing over the plain " +
            "BackgroundService ScheduleSweeperService already is. Re-adding the package without also solving the " +
            "migration gap reintroduces a dependency that cannot boot.");
    }

    /// <summary>
    ///     Phase 1.7 replaced FluentValidation with ZeroAlloc.Validation. Belongs here, not in
    ///     <c>Daedalus.Tests.Integration</c>, precisely because it is a pure file-system scan with no database
    ///     dependency: this project's suite is the one that runs on every ordinary task-verification pass, whereas
    ///     the container-bound Integration suite runs only at batch boundaries. A ban sitting only in Integration
    ///     would not catch a reintroduced FluentValidation reference until much later than it should.
    /// </summary>
    [Fact]
    public void FluentValidation_is_absent_from_every_project_and_from_central_package_management()
    {
        var offenders = FindCsprojFilesOutsideWorktrees()
            .Append(Path.Combine(FindRepositoryRoot(), "Directory.Packages.props"))
            .Where(f => File.ReadAllText(f).Contains("FluentValidation", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"FluentValidation was reintroduced in: {string.Join(", ", offenders)}");
    }

    /// <summary>
    ///     Phase 1.7's whole point: CSharpFunctionalExtensions is now unused everywhere in the solution (tasks 9
    ///     through 14 migrated every caller), and this is the rule that makes its return impossible rather than
    ///     merely undocumented. Belongs here, not in <c>Daedalus.Tests.Integration</c>, for the same reason as
    ///     <see cref="FluentValidation_is_absent_from_every_project_and_from_central_package_management"/>: this
    ///     project's suite runs on every ordinary task-verification pass, whereas the container-bound Integration
    ///     suite runs only at batch boundaries.
    /// </summary>
    [Fact]
    public void CSharpFunctionalExtensions_is_absent_from_every_project_and_from_central_package_management()
    {
        var offenders = FindCsprojFilesReferencing("CSharpFunctionalExtensions");

        var packagesPropsPath = Path.Combine(FindRepositoryRoot(), "Directory.Packages.props");
        if (File.ReadAllText(packagesPropsPath).Contains("CSharpFunctionalExtensions", StringComparison.Ordinal))
        {
            offenders.Add(packagesPropsPath);
        }

        Assert.True(offenders.Count == 0,
            $"CSharpFunctionalExtensions was reintroduced in: {string.Join(", ", offenders)}");
    }

    /// <summary>
    ///     Finds every <c>.csproj</c> under <see cref="FindRepositoryRoot"/> whose text contains
    ///     <paramref name="packageName"/>. A text scan, not an ArchUnitNET rule, because the whole point of the two
    ///     callers above is to catch a reference that would make ArchUnitNET's own namespace-based rules vacuous.
    /// </summary>
    private static List<string> FindCsprojFilesReferencing(string packageName) =>
        FindCsprojFilesOutsideWorktrees()
            .Where(p => File.ReadAllText(p).Contains(packageName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    ///     Enumerates every <c>.csproj</c> under <see cref="FindRepositoryRoot"/>, excluding worktree checkouts
    ///     via <see cref="IsOutsideWorktrees"/>, and asserts a plausible floor on how many were found. Every ban
    ///     test in this class — <c>ZeroAlloc.Saga</c>, <c>ZeroAlloc.Scheduling</c>, <c>FluentValidation</c>, and
    ///     <c>CSharpFunctionalExtensions</c> — is built on this scan. If <see cref="IsOutsideWorktrees"/> ever
    ///     filtered out every <c>.csproj</c> in the repository (for example, if this repository were ever cloned
    ///     into a directory literally named <c>worktrees</c>), each of those bans would pass having scanned
    ///     nothing, silently stop guarding against the dependency they ban being reintroduced. This solution has
    ///     19 <c>.csproj</c> files today; 15 is a safe floor that will not false-positive on ordinary project
    ///     churn while still catching a scan that came back empty or near-empty.
    /// </summary>
    private static List<string> FindCsprojFilesOutsideWorktrees()
    {
        var files = Directory
            .EnumerateFiles(FindRepositoryRoot(), "*.csproj", SearchOption.AllDirectories)
            .Where(IsOutsideWorktrees)
            .ToList();

        files.Count.Should().BeGreaterThanOrEqualTo(15,
            "IsOutsideWorktrees must not filter out (nearly) every .csproj in the repository — if it does, " +
            "every ban test built on this scan passes having scanned nothing");

        return files;
    }

    /// <summary>
    ///     Excludes <c>.claude/worktrees/...</c> — a second git worktree checkout of this same repository,
    ///     potentially on a different branch with different package references — from a repository-wide file
    ///     scan. Without this, a stale or in-progress worktree checkout can make a "must not be referenced"
    ///     assertion false-positive on a reference that exists only in that other checkout, not in the branch
    ///     actually under test. This was a latent gap in <see cref="FindCsprojFilesReferencing"/> before phase
    ///     1.7 task 6 — it happened not to be tripped because no worktree csproj referenced ZeroAlloc.Saga or
    ///     ZeroAlloc.Scheduling, but a worktree checkout's <c>Directory.Packages.props</c> did already contain
    ///     the literal string "FluentValidation", which is what surfaced the gap.
    /// </summary>
    private static bool IsOutsideWorktrees(string path) =>
        !path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(segment => segment is ".claude" or "worktrees");

    /// <summary>Walks up from <see cref="AppContext.BaseDirectory"/> to the directory containing <c>Daedalus.sln</c>.</summary>
    /// <exception cref="InvalidOperationException">No ancestor directory contains <c>Daedalus.sln</c>.</exception>
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Daedalus.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not find Daedalus.sln walking up from {AppContext.BaseDirectory}.");
    }
}
