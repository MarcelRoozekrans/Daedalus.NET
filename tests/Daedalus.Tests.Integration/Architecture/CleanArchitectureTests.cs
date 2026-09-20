using System.Reflection;
using Daedalus.Api.Middleware;
using Daedalus.Application.DTOs;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using ZeroAlloc.Validation;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Architecture;

/// <summary>
///     Phase 1.7 enforcement: FluentValidation must not return, and every DTO carrying
///     <see cref="ValidateAttribute"/> must have a live <see cref="IValidationAdapter"/> registration.
/// </summary>
/// <remarks>
///     <para>
///         <c>ZeroAlloc.Validation.Inject</c>'s <c>AddZeroAllocValidators()</c> only discovers
///         <c>class</c>-declared <c>[Validate]</c> targets — its generator predicate tests for
///         <c>ClassDeclarationSyntax</c> — and every validated DTO in this solution is a <c>record</c>. So
///         registration is explicit and hand-maintained in two places: the <c>ValidatorFor&lt;T&gt;</c>
///         singletons in <c>Daedalus.Application.Extensions.ApplicationServiceExtensions</c>, and the
///         <see cref="IValidationAdapter"/> registrations in <c>Daedalus.Api</c>'s <c>Program.cs</c>. A DTO that
///         gains <c>[Validate]</c> but no adapter registration is never validated — no exception, no 500, the
///         request just succeeds when it should have been rejected. <see cref="Every_validated_dto_has_a_registered_validation_adapter"/>
///         is the only guard that would catch that drift, which is why it is asserted against the adapter set
///         (what <c>ZeroAllocValidationFilter</c> actually consults at request time) rather than the
///         <c>ValidatorFor&lt;T&gt;</c> set.
///     </para>
///     <para>
///         This lives in the Integration project, not alongside the ArchUnitNET-based
///         <c>Daedalus.Tests.Unit.Architecture.CleanArchitectureTests</c>, because proving the adapter set
///         requires the real DI container <c>Daedalus.Api</c>'s <c>Program</c> builds — the
///         <see cref="IValidationAdapter"/> registrations are top-level statements on <c>builder.Services</c>,
///         not exposed any other way. <see cref="ApiWebApplicationFactory"/> is the existing seam for booting
///         that container in-process, hence the <see cref="PostgresFixture"/> dependency below.
///     </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class CleanArchitectureTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly IAgentRuntime _runtime = Substitute.For<IAgentRuntime>();
    private ApiWebApplicationFactory _factory = null!;
    private IServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _factory = new ApiWebApplicationFactory(fixture.ConnectionString, _runtime);
        _serviceProvider = _factory.Services;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    /// <summary>What would break this: a <c>PackageReference</c> or <c>PackageVersion</c> for FluentValidation
    /// added back to any <c>.csproj</c> or to <c>Directory.Packages.props</c>.</summary>
    [Fact]
    public void FluentValidation_is_absent_from_every_project_and_from_central_package_management()
    {
        var offenders = Directory
            .EnumerateFiles(FindRepositoryRoot(), "*.csproj", SearchOption.AllDirectories)
            .Where(IsOutsideWorktrees)
            .Append(Path.Combine(FindRepositoryRoot(), "Directory.Packages.props"))
            .Where(f => File.ReadAllText(f).Contains("FluentValidation", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"FluentValidation was reintroduced in: {string.Join(", ", offenders)}");
    }

    /// <summary>What would break this: adding <c>[Validate]</c> to a DTO in <c>Daedalus.Application</c> and
    /// forgetting the matching <c>AddSingleton&lt;IValidationAdapter, ValidationAdapter&lt;T&gt;&gt;()</c> line
    /// in <c>Daedalus.Api</c>'s <c>Program.cs</c>. Verified red/green manually — see task-6-report.md.</summary>
    [Fact]
    public void Every_validated_dto_has_a_registered_validation_adapter()
    {
        var validated = typeof(CreateTaskDto).Assembly.GetTypes()
            .Where(t => Attribute.IsDefined(t, typeof(ValidateAttribute), inherit: false))
            .ToList();

        var registered = _serviceProvider.GetServices<IValidationAdapter>()
            .Select(a => a.TargetType)
            .ToHashSet();

        var unregistered = validated.Where(t => !registered.Contains(t)).Select(t => t.Name).ToList();

        Assert.True(unregistered.Count == 0,
            $"[Validate] DTOs with no registered adapter, so they are never validated: {string.Join(", ", unregistered)}");
    }

    /// <summary>
    ///     Excludes the git worktree(s) under <c>.claude/worktrees/</c> — a second checkout of this repository
    ///     (potentially on a different branch, with its own stale <c>Directory.Packages.props</c>) that would
    ///     otherwise produce confusing duplicate offenders or false positives when this scan walks the whole
    ///     repository root.
    /// </summary>
    private static bool IsOutsideWorktrees(string path) =>
        !path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(segment => segment is ".claude" or "worktrees");

    /// <summary>Walks up from <see cref="AppContext.BaseDirectory"/> to the directory containing <c>Daedalus.sln</c>.
    /// Mirrors <c>Daedalus.Tests.Unit.Architecture.CleanArchitectureTests.FindRepositoryRoot</c> exactly; duplicated
    /// here rather than shared because that method is private to a different test assembly.</summary>
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
