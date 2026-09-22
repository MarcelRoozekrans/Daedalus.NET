using Microsoft.Extensions.Configuration;

namespace Daedalus.Tests.Unit.Configuration;

/// <summary>
///     Pins the <c>ScheduledRuns</c>, <c>DetachedRuns</c> and <c>Thalos:ToolPolicies</c> sections of the Api and Cli
///     hosts' <c>appsettings.json</c> files against each other.
/// </summary>
/// <remarks>
///     Both hosts register <c>ScheduleReconciler</c> (via <c>AddDaedalusAgents</c>) and point at the same
///     <c>daedalus</c> database. Reconciliation disables — never deletes — any <c>ScheduleOrigin.Config</c>
///     row whose name is missing from the host's own configuration, so if the Cli's <c>ScheduledRuns</c> array were
///     ever empty (or otherwise out of step with the Api's), simply starting the Cli would find zero configured
///     names and disable every config-origin schedule the Api created. A code comment is currently the only guard
///     against that drift (see both <c>appsettings.json</c> files' <c>_comment_ScheduledRuns</c> entries); this test
///     converts it into a build failure instead.
///     <para>
///     <c>Thalos:ToolPolicies</c> is pinned for a related reason: both hosts register <c>DaedalusRepoActionTools</c>
///     (via <c>AddDaedalusAgents</c>), and <c>DefaultToolAuthorizer</c> is the only thing standing between an
///     agent's <c>Tools</c> list and an actual write against a real repository. A host missing the
///     <c>repoaction__*</c> → <c>developer</c> binding would defend those tools by agent configuration alone —
///     the exact brittleness this policy layer exists to remove.
///     </para>
/// </remarks>
public sealed class ScheduledRunsConfigurationDriftTests
{
    private static IConfiguration Load(string fileName) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(fileName, optional: false)
            .Build();

    /// <summary>Flattens a section to its descendant key/value pairs, relative to the section itself.</summary>
    private static Dictionary<string, string?> Flatten(IConfiguration configuration, string sectionKey) =>
        configuration.GetSection(sectionKey)
            .AsEnumerable(makePathsRelative: true)
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    [Theory]
    [InlineData("ScheduledRuns")]
    [InlineData("DetachedRuns")]
    [InlineData("Thalos:ToolPolicies")]
    public void Api_and_Cli_agree_on_the_scheduling_section(string sectionKey)
    {
        var api = Flatten(Load("Daedalus.Api.appsettings.json"), sectionKey);
        var cli = Flatten(Load("Daedalus.Cli.appsettings.json"), sectionKey);

        api.Should().NotBeEmpty($"Daedalus.Api/appsettings.json must configure {sectionKey}");
        cli.Should().BeEquivalentTo(api,
            $"Daedalus.Api/appsettings.json and Daedalus.Cli/appsettings.json must configure identical " +
            $"{sectionKey} sections — both hosts reconcile against the same database, and a Cli whose " +
            $"{sectionKey} drifted from the Api's would silently disable or corrupt config-origin schedules " +
            "the Api created");
    }

    /// <summary>
    ///     Pins <c>Daedalus.Cli/appsettings.json</c>'s <c>Thalos:Workflow:Enabled</c> to <see langword="false"/> —
    ///     deliberately the one setting in this file that must <em>not</em> match the Api's. <c>Daedalus.Cli</c>
    ///     is not orchestrated by the AppHost and has no <c>WaitForCompletion(migrations)</c> step, and even
    ///     where the workflow tables do exist, a second poller against the same outbox table as
    ///     <c>Daedalus.Api</c> only buys duplicate paid agent turns — <c>FetchPendingAsync</c> has no
    ///     <c>FOR UPDATE SKIP LOCKED</c>, so both hosts can fetch and dispatch the same row and both pay for the
    ///     turn. See <c>WorkflowConfig.Enabled</c>'s own remarks. Without this test, deleting or flipping that
    ///     one line in the real file is invisible to every other test in this solution:
    ///     <c>CliHostSchedulingWiringTests</c> deliberately builds its own minimal configuration that never sets
    ///     this key (to exercise <c>WorkflowConfig.Enabled</c>'s compiled-in default), so it never reads this
    ///     file at all.
    /// </summary>
    [Fact]
    public void Cli_disables_the_workflow_engine()
    {
        var workflow = Flatten(Load("Daedalus.Cli.appsettings.json"), "Thalos:Workflow");

        workflow.Should().ContainKey("Enabled", "Daedalus.Cli/appsettings.json must set Thalos:Workflow:Enabled explicitly");
        bool.Parse(workflow["Enabled"]!).Should().BeFalse(
            "a second poller against the same outbox table as Daedalus.Api only buys duplicate paid agent " +
            "turns, and Daedalus.Cli has no WaitForCompletion(migrations) guarantee the tables even exist");
    }
}
