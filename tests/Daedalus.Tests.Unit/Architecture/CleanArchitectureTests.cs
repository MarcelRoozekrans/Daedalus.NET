using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Persistence;
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
    private static readonly SysAssembly DomainAssembly = typeof(Task).Assembly;
    private static readonly SysAssembly ApplicationAssembly = typeof(ITaskRepository).Assembly;
    private static readonly SysAssembly InfrastructureAssembly = typeof(ApplicationDbContext).Assembly;
    private static readonly SysAssembly ApiAssembly = typeof(Api.Program).Assembly;

    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(DomainAssembly, ApplicationAssembly, InfrastructureAssembly, ApiAssembly)
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
}
