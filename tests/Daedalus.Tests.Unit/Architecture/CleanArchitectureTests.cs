using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Daedalus.Agents;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Persistence;
using Rag.NET.Abstractions;
using Rag.NET.PgVector;
using Thalos;
using Thalos.Anthropic;
using Thalos.Mcp;
using Thalos.Memory;
using Thalos.Memory.RagNet;
using Thalos.Sentinel;
using Thalos.Skills;
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

    private static readonly SysAssembly DomainAssembly = typeof(Task).Assembly;
    private static readonly SysAssembly ApplicationAssembly = typeof(ITaskRepository).Assembly;
    private static readonly SysAssembly InfrastructureAssembly = typeof(ApplicationDbContext).Assembly;
    private static readonly SysAssembly ApiAssembly = typeof(Api.Program).Assembly;
    private static readonly SysAssembly AgentsAssembly = typeof(DaedalusAgentsServiceCollectionExtensions).Assembly;
    private static readonly SysAssembly WebAssembly = typeof(Daedalus.Web.App).Assembly;

    private static readonly SysAssembly MemoryRagNetAssembly = typeof(RagNetMemoryOptions).Assembly; // Thalos.NET.Memory.RagNet

    private static readonly SysAssembly[] ThalosAssemblies =
    [
        typeof(IAgentRuntime).Assembly, // Thalos.NET.Abstractions
        typeof(ThalosBuilder).Assembly, // Thalos.NET
        typeof(AnthropicThalosBuilderExtensions).Assembly, // Thalos.NET.Anthropic
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
        .LoadAssemblies(ThalosAssemblies)
        .LoadAssemblies(RagNetAssemblies)
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
}
