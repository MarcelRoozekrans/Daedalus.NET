using System.Reflection;
using System.Text;
using Daedalus.Agents;
using Daedalus.Agents.Tools;
using Daedalus.Tests.Integration.Fixtures;
using Daedalus.Tests.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Thalos.Git;
using Thalos.Tools;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Phase 3.2, Task 21: Daedalus registers its Thalos tools and Thalos' git action tools through typed binders, not
///     through the reflective <c>AddLocalTools(name, typeof(...))</c>. These guards read the real Api host's tool catalog:
///     no Daedalus source is the reflective one, every <c>[ThalosTool]</c> method is bound, and the catalog matches the
///     literals <see cref="ThalosToolOracleTests"/> captured from the reflective registration.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ThalosToolRegistryGuardTests(PostgresFixture fixture) : IAsyncLifetime
{
    /// <summary>The source each tool class Daedalus registers is registered under.</summary>
    private static readonly (Type ToolType, string Source)[] Registered =
    [
        (typeof(DaedalusKnowledgeTools), DaedalusAgentsServiceCollectionExtensions.KnowledgeToolSourceName),
        (typeof(DaedalusScheduleTools), DaedalusAgentsServiceCollectionExtensions.KnowledgeToolSourceName),
        (typeof(DaedalusRepoTools), DaedalusAgentsServiceCollectionExtensions.KnowledgeToolSourceName),
        (typeof(DaedalusReviewTools), DaedalusAgentsServiceCollectionExtensions.KnowledgeToolSourceName),
        (typeof(DaedalusRepoActionTools), DaedalusAgentsServiceCollectionExtensions.RepoActionToolSourceName),
        (typeof(DaedalusIssueTools), DaedalusAgentsServiceCollectionExtensions.IssuesToolSourceName),
        (typeof(DaedalusManufactureTools), DaedalusAgentsServiceCollectionExtensions.ManufactureToolSourceName),
        (typeof(GitActionTools), DaedalusAgentsServiceCollectionExtensions.GitToolSourceName),
    ];

    private ApiWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _factory = ThalosToolOracleTests.CreateFactory(fixture.ConnectionString);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    ///     Every source under a Daedalus name is the typed form. The reflective <see cref="LocalToolSource"/> is the one
    ///     public source type; the typed one is Thalos' internal generic, so this is checked by exclusion.
    /// </summary>
    [Fact]
    public void No_Daedalus_tool_source_is_reflective()
    {
        using var scope = _factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetServices<IToolSource>()
            .Where(s => ThalosToolOracleTests.DaedalusSources.Contains(s.Name, StringComparer.Ordinal))
            .ToList();

        // Not defensive: with no source found, the check below would hold vacuously.
        // Red: rename GitToolSourceName's registration to "git2"; seven sources are found, not eight.
        sources.Should().HaveCount(8, "four classes share the daedalus source, and the other four have one source each");

        // Red: put the issues source back on the reflective overload that takes the tool class as a Type; that source
        // is then a LocalToolSource.
        sources.Where(s => s is LocalToolSource).Select(s => s.Name).Should().BeEmpty();
    }

    /// <summary>
    ///     Each <c>[ThalosTool]</c> method of each <c>[ThalosToolType]</c> class Daedalus registers is in the catalog, under
    ///     its class's source and its declared name.
    /// </summary>
    [Fact]
    public async Task Every_ThalosTool_method_is_registered()
    {
        // Red: remove the DaedalusIssueTools row from Registered; the Agents assembly holds one tool class the table
        // does not.
        Registered.Select(r => r.ToolType).Where(t => t.Assembly == typeof(DaedalusAgentsServiceCollectionExtensions).Assembly)
            .Should().BeEquivalentTo(DaedalusToolTypes(), "every Daedalus tool class must be registered under a known source");

        var declared = Registered.SelectMany(r => DeclaredToolNames(r.ToolType).Select(name => $"{r.Source}__{name}")).ToList();

        // Not defensive: an empty declared list would make the subset check vacuous. Checked before the catalog is read,
        // since a binder naming a method without the attribute fails that read.
        // Red: remove the [ThalosTool] attribute from DaedalusRepoActionTools.AddLabel; 16 tool methods are declared.
        declared.Should().HaveCount(17);

        var catalog = await CatalogAsync();

        // Red: drop the t => t.AddLabel binder from the repoaction registration; repoaction__add_label is missing.
        catalog.Select(t => t.Name).Should().Contain(declared);
    }

    /// <summary>
    ///     The catalog serves the typed tools in the reflective registration's order, with its names, descriptions and
    ///     schemas, and each tool answers with its bytes, in M.E.AI's encoder, for the characters Review Focus 3 names.
    /// </summary>
    [Fact]
    public async Task The_typed_tools_match_the_reflective_oracle()
    {
        var catalog = await CatalogAsync();
        var callable = await CallableAsync();
        var expectedNames = ThalosToolOracleTests.ExpectedResults
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line[..line.IndexOf(" => ", StringComparison.Ordinal)]);

        // Red: register DaedalusReviewTools before DaedalusKnowledgeTools on the daedalus source; the first
        // daedalus__ name is report_review_outcome. Duplicates, if any, are dropped first-wins in this order too.
        catalog.Select(t => t.Name).Should().Equal(expectedNames);

        var dump = new StringBuilder();
        string? current = null;
        foreach (var tool in catalog)
        {
            var source = tool.Name[..tool.Name.IndexOf("__", StringComparison.Ordinal)];
            if (!string.Equals(source, current, StringComparison.Ordinal))
            {
                dump.Append('[').Append(source).Append("]\n");
                current = source;
            }

            // The catalog's wrapper exposes its tool's description and schemas under the qualified name.
            dump.Append(ToolDump.Line(tool).AsSpan((source + "__").Length)).Append('\n');
        }

        // Red: remove the [Description] from DaedalusIssueTools.Get's number parameter; issues__get's schema loses it.
        // The registration's options do not reach the dump: neither the context's own generation options nor a
        // NumberHandling on the tool options changes a schema, so the dump holds what the binders bind.
        dump.ToString().Should().Be(ThalosToolOracleTests.ExpectedDump);

        var results = new StringBuilder();
        foreach (var tool in catalog)
        {
            results.Append(tool.Name).Append(" => ").Append(await ThalosToolOracleTests.InvokeAsync(callable[tool.Name])).Append('\n');
        }

        // Red: pass DaedalusToolsJsonContext.Default.Options instead of LocalToolSources.CreateOptions to the typed
        // registrations; the echoed characters come back escaped by the default encoder.
        results.ToString().Should().Be(ThalosToolOracleTests.ExpectedResults);
    }

    /// <summary>The catalog's tools from the Daedalus sources, for an agent allowed exactly those, as a run's agent sees them.</summary>
    private async Task<IReadOnlyList<AITool>> CatalogAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var agent = new AgentDefinition
        {
            Id = new AgentId(Guid.NewGuid()),
            Name = "registry-guard",
            Instructions = "Unused.",
            Tools = [.. ThalosToolOracleTests.DaedalusSources.Select(s => s + "__*")],
        };

        var tools = await scope.ServiceProvider.GetRequiredService<IToolCatalog>().ResolveAsync(agent, CancellationToken.None);
        tools.IsSuccess.Should().BeTrue("every Daedalus tool source must enumerate");
        return tools.Value;
    }

    /// <summary>
    ///     The sources' own tools by qualified name, the first of a name winning as in the catalog: the catalog's wrapper
    ///     would put each call through the shipped tool policies, which refuse the anonymous caller a test has.
    /// </summary>
    private async Task<Dictionary<string, AIFunction>> CallableAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var callable = new Dictionary<string, AIFunction>(StringComparer.Ordinal);
        foreach (var source in scope.ServiceProvider.GetServices<IToolSource>()
                     .Where(s => ThalosToolOracleTests.DaedalusSources.Contains(s.Name, StringComparer.Ordinal)))
        {
            var tools = await source.GetToolsAsync(CancellationToken.None);
            tools.IsSuccess.Should().BeTrue($"the '{source.Name}' tool source must enumerate");
            foreach (var tool in tools.Value.Cast<AIFunction>())
            {
                callable.TryAdd($"{source.Name}__{tool.Name}", tool);
            }
        }

        return callable;
    }

    private static IEnumerable<Type> DaedalusToolTypes() =>
        typeof(DaedalusAgentsServiceCollectionExtensions).Assembly.GetTypes()
            .Where(t => t.IsDefined(typeof(ThalosToolTypeAttribute), inherit: false));

    private static IEnumerable<string> DeclaredToolNames(Type toolType) =>
        toolType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<ThalosToolAttribute>()))
            .Where(m => m.Attribute is not null)
            .Select(m => m.Attribute!.Name ?? m.Method.Name);
}
