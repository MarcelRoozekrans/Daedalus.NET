using System.Reflection;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Daedalus.Tests.Unit.Infrastructure.Persistence;

/// <summary>
///     <see cref="ApplicationDbContext"/> holds only the outbox table now. The golden file holds that model as its
///     debug view with long annotations, so any drift in an index, converter or concurrency token shows up as a diff.
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
    public void Model_matches_the_golden()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(GoldenResource)!;
        using var reader = new StreamReader(stream);
        var golden = WithoutProductVersion(reader.ReadToEnd());

        var actual = WithoutProductVersion(BuildModel().ToDebugString(MetadataDebugStringOptions.LongDefault));

        // Red: change the outbox table's index in the model, and this
        // assertion fails with a diff. After a deliberate model change, refresh the golden from the actual text,
        // without its ProductVersion line.
        actual.Should().Be(golden);
    }

    [Fact]
    public void Model_comparison_ignores_the_EF_Core_product_version()
    {
        const string Dump = "Model: \n  Annotations: \n  ProductVersion: 10.0.12\n  Relational:MaxIdentifierLength: 63";
        var bumped = Dump.Replace("10.0.12", "10.0.99", StringComparison.Ordinal);

        // Red: return the text unchanged from WithoutProductVersion, and the two versions no longer compare equal.
        // Renovate automerges EF Core patch bumps, so the golden must not hold the version that built it.
        WithoutProductVersion(bumped).Should().Be(WithoutProductVersion(Dump));

        // Red: drop every annotation line instead of only ProductVersion, and the next annotation disappears.
        WithoutProductVersion(Dump).Should().Contain("Relational:MaxIdentifierLength: 63");
    }

    /// <summary>
    ///     The model's <c>ProductVersion</c> annotation is the EF Core version that built it. It says nothing about the
    ///     configurations, and it changes on every EF Core patch, so both sides of the comparison drop it.
    /// </summary>
    private static string WithoutProductVersion(string debugView) =>
        string.Join('\n', debugView.ReplaceLineEndings("\n").Split('\n')
            .Where(line => !line.TrimStart().StartsWith("ProductVersion: ", StringComparison.Ordinal)));

    [Fact]
    public void Model_has_every_entity_type()
    {
        // Red: remove the outbox table registration from OnModelCreating, and the count drops to zero.
        BuildModel().GetEntityTypes().Should().HaveCount(1);
    }
}
