using System.Reflection;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Daedalus.Tests.Unit.Infrastructure.Persistence;

/// <summary>
///     <see cref="ApplicationDbContext"/> applies its entity configurations from an explicit list, because an
///     assembly scan is not trim-safe. The golden file holds the model the assembly scan built, as its debug view
///     with long annotations, so any drift in an index, converter, concurrency token or owned type shows up as a diff.
/// </summary>
public class ApplicationDbContextModelTests
{
    private const string GoldenResource = "ApplicationDbContextModel.golden.txt";

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=model-test;Database=model-test").Options;
        using var context = new ApplicationDbContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public void Model_matches_the_golden_built_by_the_old_assembly_scan()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(GoldenResource)!;
        using var reader = new StreamReader(stream);
        var golden = reader.ReadToEnd().ReplaceLineEndings("\n");

        var actual = BuildModel().ToDebugString(MetadataDebugStringOptions.LongDefault).ReplaceLineEndings("\n");

        // Red: change the maximum length of one column in TaskConfiguration, or drop one of its indexes, and this
        // assertion fails with a diff. After a deliberate model change, refresh the golden from the actual text.
        actual.Should().Be(golden);
    }

    [Fact]
    public void Model_has_every_entity_type()
    {
        // Red: remove the outbox table registration from OnModelCreating, or a configuration that declares an entity,
        // and the count drops.
        BuildModel().GetEntityTypes().Should().HaveCount(24);
    }

    [Fact]
    public void Every_entity_configuration_in_the_assembly_is_in_the_explicit_list()
    {
        var inAssembly = typeof(ApplicationDbContext).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && t.GetInterfaces().Any(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>)))
            .ToList();
        inAssembly.Should().NotBeEmpty("the reflection must find the configurations, or this guard checks nothing");

        var listed = ApplicationDbContext.Configurations.Select(c => c.Configuration).ToHashSet();
        var missing = inAssembly.Where(t => !listed.Contains(t)).Select(t => t.FullName).ToList();

        // Red: add a configuration class for the Skill entity to Infrastructure without listing it, and this fails
        // naming that class.
        missing.Should().BeEmpty("ApplicationDbContext.Configurations must list every configuration; missing: {0}",
            string.Join(", ", missing));
    }
}
