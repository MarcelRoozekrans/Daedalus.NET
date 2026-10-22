using System.ComponentModel;
using System.Reflection;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Agents;
using Daedalus.Tests.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace Daedalus.Tests.Unit.Infrastructure.Agents;

/// <summary>
///     Phase 3.2, Task 18: <see cref="McpToolBuilder"/> builds its local tools from an explicit list, not from an assembly
///     scan. These guards keep the list complete and hold it to the reflective registration it replaced, which survives
///     here as the oracle.
/// </summary>
public sealed class McpToolRegistryGuardTests
{
    /// <summary>Every character Review Focus 3 names, so a change of encoder shows in the result bytes.</summary>
    private const string Awkward = "<a & 'é' 🚀>";

    private readonly ILearningsMemory _memory = Substitute.For<ILearningsMemory>();
    private readonly IFailurePatternDatabase _failures = Substitute.For<IFailurePatternDatabase>();

    public McpToolRegistryGuardTests()
    {
        _memory.RecallAsync(Awkward, 5, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<RecalledLearning>>.Success(
            [new RecalledLearning("m1", Awkward, [Awkward], 0.12345, new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero))]));
        _memory.RecallAsync("down", 5, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RecalledLearning>>.Failure(Awkward));
        _failures.SearchByErrorAsync(Awkward, 3, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<FailurePatternRecord>>.Success(
            [new FailurePatternRecord
            {
                ErrorText = Awkward,
                Resolution = Awkward,
                SourceTaskId = new Guid(0xb7f3c1e, 0x5a2d, 0x4e8f, 0x9c, 0x6b, 0x1d, 0x2e, 0x3f, 0x4a, 0x5b, 0x6c),
                ErrorIteration = 2,
                ResolutionIteration = null,
                ObservedAt = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc),
            }]));
    }

    /// <summary>
    ///     The list names exactly the <c>[McpServerToolType]</c> classes the old scan found, in the scan's order, and every
    ///     <c>[McpServerTool]</c> method on them is built under its declared name.
    /// </summary>
    [Fact]
    public async Task Every_McpServerTool_method_is_listed()
    {
        // Red: remove the DaedalusLearningsTools entry from McpToolBuilder.LocalTools; the scanned types then hold
        // one class the list does not.
        McpToolBuilder.LocalTools.Select(t => t.ToolType).Should().Equal(ScannedToolTypes());

        await using var provider = Provider();
        await using var builder = new McpToolBuilder(provider, NullLogger<McpToolBuilder>.Instance);
        var built = (await LocalToolsAsync(builder, "daedalus")).Select(t => t.Name);

        // Red: build DaedalusLearningsTools' tool under the name "search_learnings_typed"; the declared name is missing.
        built.Should().Equal(ScannedToolTypes().SelectMany(DeclaredToolNames));
    }

    /// <summary>
    ///     The explicit list dumps to the same names, descriptions, schemas and order as the reflective registration, and
    ///     each tool returns the same bytes: a raw unavailable answer, and an escaped serialised result. Both paths call
    ///     the same tool method, so these hold the builder's wiring and its encoder; the serialised result's own bytes
    ///     are pinned as literals in <see cref="McpToolOracleTests"/>.
    /// </summary>
    [Fact]
    public async Task The_built_tools_match_the_reflective_oracle()
    {
        await using var provider = Provider();
        await using var builder = new McpToolBuilder(provider, NullLogger<McpToolBuilder>.Instance);
        var built = await LocalToolsAsync(builder, "daedalus");
        var oracle = ReflectiveOracle(provider);

        // Red: swap the order of the two entries in McpToolBuilder.LocalTools; the dump's lines come out swapped.
        ToolDump.Dump(built).Should().Be(ToolDump.Dump(oracle));

        // Red: give McpToolBuilder's tool options Encoder = JavaScriptEncoder.Default instead of M.E.AI's; the raw
        // characters of the unavailable answer are escaped.
        (await LearningsResultsAsync(built)).Should().Equal(await LearningsResultsAsync(oracle));

        // Red: pass maxResults + 1 to SearchFailurePatterns in McpToolBuilder's failure-patterns entry; the search
        // finds nothing, and the answer differs.
        (await ResultAsync(built, "search_failure_patterns", "errorMessage", Awkward))
            .Should().Be(await ResultAsync(oracle, "search_failure_patterns", "errorMessage", Awkward));
    }

    /// <summary>
    ///     Characterization: every <c>"local"</c> entry gets all the local tools, whatever its name, as it did before
    ///     the list.
    /// </summary>
    [Fact]
    public async Task Every_local_entry_gets_all_local_tools_whatever_its_name()
    {
        await using var provider = Provider();
        await using var builder = new McpToolBuilder(provider, NullLogger<McpToolBuilder>.Instance);

        var tools = await builder.BuildToolsAsync(new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["daedalus"] = new() { Type = "local" },
            ["anything-else"] = new() { Type = "LOCAL" },
        });

        // Red: build local tools only when the server name is "daedalus"; the second entry then adds nothing.
        tools.Select(t => t.Name).Should().Equal(
            "search_failure_patterns", "search_learnings", "search_failure_patterns", "search_learnings");
    }

    /// <summary>
    ///     Characterization: a tool class whose dependencies cannot be resolved is left out when the tools are built, as
    ///     the scan's probe instance left it out, and the other class's tools are still built.
    /// </summary>
    [Fact]
    public async Task A_tool_class_that_cannot_be_constructed_is_left_out()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_failures);
        await using var provider = services.BuildServiceProvider();
        await using var builder = new McpToolBuilder(provider, NullLogger<McpToolBuilder>.Instance);

        var tools = await builder.BuildToolsAsync(
            new Dictionary<string, McpServerConfig>(StringComparer.Ordinal) { ["daedalus"] = new() { Type = "local" } });

        // Red: drop the probe instance from McpToolBuilder's list entries; search_learnings is then built without
        // its ILearningsMemory and fails only when a model calls it.
        tools.Select(t => t.Name).Should().Equal("search_failure_patterns");
    }

    /// <summary>The old scan, verbatim: concrete <c>[McpServerToolType]</c> classes in the Infrastructure assembly.</summary>
    private static List<Type> ScannedToolTypes() =>
        typeof(McpToolBuilder).Assembly.GetTypes()
            .Where(t => Attribute.IsDefined(t, typeof(McpServerToolTypeAttribute)) && !t.IsAbstract)
            .ToList();

    private static IEnumerable<string> DeclaredToolNames(Type toolType) =>
        toolType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(m => m.Attribute is not null)
            .Select(m => m.Attribute!.Name ?? m.Method.Name);

    /// <summary>
    ///     The reflective registration <see cref="McpToolBuilder"/> used before Task 18: one function per
    ///     <c>[McpServerTool]</c> method, named and described from its attributes, with M.E.AI's default options.
    /// </summary>
    private static List<AITool> ReflectiveOracle(IServiceProvider provider) =>
        ScannedToolTypes()
            .SelectMany(type =>
            {
                var instance = ActivatorUtilities.CreateInstance(provider, type);
                return type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
                    .Where(m => Attribute.IsDefined(m, typeof(McpServerToolAttribute)))
                    .Select(m => (AITool)AIFunctionFactory.Create(
                        m,
                        instance,
                        m.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? m.Name,
                        m.GetCustomAttribute<DescriptionAttribute>()?.Description));
            })
            .ToList();

    /// <summary>The learnings tool's unavailable answer, then its serialised result for the awkward recall.</summary>
    private static async Task<string[]> LearningsResultsAsync(IReadOnlyList<AITool> tools) =>
    [
        await ResultAsync(tools, "search_learnings", "query", "down"),
        await ResultAsync(tools, "search_learnings", "query", Awkward),
    ];

    private static async Task<string> ResultAsync(IReadOnlyList<AITool> tools, string name, string parameter, string value)
    {
        var tool = (AIFunction)tools.Single(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        var arguments = new AIFunctionArguments(
            new Dictionary<string, object?>(StringComparer.Ordinal) { [parameter] = value }, StringComparer.Ordinal);
        return ToolDump.Result(await tool.InvokeAsync(arguments, CancellationToken.None));
    }

    private static async Task<IReadOnlyList<AITool>> LocalToolsAsync(McpToolBuilder builder, string serverName)
    {
        var tools = await builder.BuildToolsAsync(
            new Dictionary<string, McpServerConfig>(StringComparer.Ordinal) { [serverName] = new() { Type = "local" } });

        // Not defensive: an empty list would make every comparison above vacuous.
        // Red: remove the DaedalusLearningsTools entry from McpToolBuilder.LocalTools; one tool is built, not two.
        tools.Should().HaveCount(2, "both [McpServerToolType] classes carry one tool each");
        return tools;
    }

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_memory);
        services.AddSingleton(_failures);
        return services.BuildServiceProvider();
    }
}
