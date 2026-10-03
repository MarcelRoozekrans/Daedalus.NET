using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Thalos;
using Thalos.Git;
using Thalos.Testing;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.6 task B8: manufacture v7 in sandbox mode, end to end, replacing B16's allow-list proof for that mode. Real
///     Docker, the real Daedalus sandbox image built from <c>src/Daedalus.Sandbox/Dockerfile</c>, Thalos 0.14.2's sandbox
///     packages and the shipped <c>roslyn</c> MCP entry; only the model is scripted, through <see cref="ScriptedChatClient"/>,
///     so every tool call goes through the host's real runtime, tool catalog and authorizer to the run's own container.
///     Runs are started and resumed over HTTP. The pull-request host is <see cref="FakePullRequestPublisher"/>; the push
///     goes to a real <see cref="LocalGitRemote"/>.
/// </summary>
/// <remarks>
///     <para>
///     <b>The seed repository</b> is a small solution the sandbox restores, builds and tests: <c>Sandbox.sln</c>,
///     <c>src/Lib</c>, <c>tests/Lib.Tests</c> with one passing xunit test, and <c>AGENT.md</c>. Restoring it inside a sandbox
///     reaches nuget.org through the egress proxy, so the suite needs the network as well as Docker.
///     </para>
///     <para>
///     <b>Timing.</b> No assertion is about elapsed time. Every wait is a generous hang guard around an outcome, and the
///     workspace sweep that parks a sandbox is driven directly, through the host's own <see cref="RunWorkspaceSweeper"/>,
///     rather than waited for.
///     </para>
///     <para>
///     Each test's doc comment names, for every assertion, the change that turns it red. Each was applied once and
///     reverted; see the task B8 report.
///     </para>
/// </remarks>
[Collection(ManufactureSandboxCollection.Name)]
public sealed class ManufactureSandboxEndToEndTests(PostgresFixture postgres, SandboxImageFixture docker)
{
    private const string OutcomeToolName = "workflow__report_outcome";

    private const string LearnedLine = "Integration tests need Docker running.";

    // The final newline is there because Thalos's base-file reader adds one (Thalos issue #245).
    private const string PinnedStandingInstructions = "Run dotnet test.\n";

    private const string LibCsproj = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>""";

    private const string LibCsprojWithJson = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
          <ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup>
        </Project>
        """;

    private const string SeedA = "namespace Lib;\n\npublic static class A\n{\n    public static int Add(int a, int b) => a + b;\n}\n";

    private const string AddLine = "    public static int Add(int a, int b) => a + b;\n";

    private const string DescribeLine = "    public static string Describe(int x) => Newtonsoft.Json.JsonConvert.SerializeObject(new { x });\n";

    private const string ProtectedPath = ".github/workflows/x.yml";

    /// <summary>
    ///     What the S3 marker target writes. MSBuild computes it, so the text appears only where MSBuild ran: the
    ///     <c>Directory.Build.props</c> the agent writes holds its three parts, never the whole.
    /// </summary>
    private const string MarkerText = "s3-marker-ran";

    private const string MarkerProps = """
        <Project>
          <Target Name="S3Marker" BeforeTargets="CoreCompile">
            <WriteLinesToFile File="$(MSBuildThisFileDirectory)marker.txt" Lines="$([System.String]::Concat('s3-', 'marker-', 'ran'))" Overwrite="true" />
          </Target>
        </Project>
        """;

    /// <summary>
    ///     A test that finds the marker above its own output directory, inside the sandbox, and asserts it is there. Its
    ///     argument is what the marker holds, read when the test is discovered, so the text is in the test's name; the test
    ///     then fails on purpose, because <c>dotnet test -v:q</c> names only failed tests in the output a sandbox result
    ///     carries.
    /// </summary>
    private const string MarkerTest = """
        namespace Lib.Tests;

        public class MarkerTests
        {
            public static System.Collections.Generic.IEnumerable<object[]> Marker()
            {
                var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
                while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "marker.txt")))
                {
                    dir = dir.Parent;
                }

                yield return [dir is null ? "absent" : System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "marker.txt")).Trim()];
            }

            [Xunit.Theory]
            [Xunit.MemberData(nameof(Marker))]
            public void Marker_inside(string marker)
            {
                Xunit.Assert.NotEqual("absent", marker);
                Xunit.Assert.Fail("fails on purpose, so the quiet output names this test and its argument");
            }
        }
        """;

    private static readonly string[] Lenses = ["correctness", "falsifiability", "mechanism"];

    /// <summary>A hang guard for a run to reach its gate: a sandbox create, a NuGet restore, a Roslyn load and a test run.</summary>
    private static readonly TimeSpan RunGuard = TimeSpan.FromMinutes(20);

    /// <summary>A hang guard for the publish after a resume or a retry.</summary>
    private static readonly TimeSpan PublishGuard = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyList<(string Path, string Content)> Seed =
    [
        ("AGENT.md", PinnedStandingInstructions),
        (".gitignore", "bin/\nobj/\nmarker.txt\n"),
        ("Sandbox.sln", """

            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "src\Lib\Lib.csproj", "{6E2C5B1A-4E0B-4C57-9C3E-2F1D6A8B7C11}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib.Tests", "tests\Lib.Tests\Lib.Tests.csproj", "{7F3D6C2B-5F1C-4D68-8D4F-3A2E7B9C8D22}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            		Release|Any CPU = Release|Any CPU
            	EndGlobalSection
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            		{6E2C5B1A-4E0B-4C57-9C3E-2F1D6A8B7C11}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{6E2C5B1A-4E0B-4C57-9C3E-2F1D6A8B7C11}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{6E2C5B1A-4E0B-4C57-9C3E-2F1D6A8B7C11}.Release|Any CPU.ActiveCfg = Release|Any CPU
            		{6E2C5B1A-4E0B-4C57-9C3E-2F1D6A8B7C11}.Release|Any CPU.Build.0 = Release|Any CPU
            		{7F3D6C2B-5F1C-4D68-8D4F-3A2E7B9C8D22}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{7F3D6C2B-5F1C-4D68-8D4F-3A2E7B9C8D22}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{7F3D6C2B-5F1C-4D68-8D4F-3A2E7B9C8D22}.Release|Any CPU.ActiveCfg = Release|Any CPU
            		{7F3D6C2B-5F1C-4D68-8D4F-3A2E7B9C8D22}.Release|Any CPU.Build.0 = Release|Any CPU
            	EndGlobalSection
            EndGlobal
            """),
        ("src/Lib/Lib.csproj", LibCsproj),
        ("src/Lib/A.cs", SeedA),
        ("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
              </ItemGroup>
              <ItemGroup><ProjectReference Include="..\..\src\Lib\Lib.csproj" /></ItemGroup>
            </Project>
            """),
        ("tests/Lib.Tests/ATests.cs", "namespace Lib.Tests;\n\npublic class ATests\n{\n    [Xunit.Fact]\n    public void Adds() => Xunit.Assert.Equal(3, Lib.A.Add(1, 2));\n}\n"),
    ];

    /// <summary>
    ///     Replaces B16 for sandbox mode: implement writes a <c>.csproj</c>, which the sandbox allows, is refused a protected
    ///     path, edits code and runs the tests in its sandbox; the run parks at the gate, its sandbox is parked and gone,
    ///     and the approved run publishes the change.
    /// </summary>
    /// <remarks>
    ///     Reds, per assertion:
    ///     <list type="bullet">
    ///         <item>the one protected refusal: drop <c>.github/</c> from the effective protected set, which is not
    ///         configurable (ruling R44); applied instead as writing <c>docs/x.yml</c>, which is not protected, in the
    ///         script, and the refusal is then absent;</item>
    ///         <item>the test-result record with exit 0: unregister <c>SandboxCallRecorder</c> in sandbox mode, and none is
    ///         recorded;</item>
    ///         <item>no container of the run after the sweeps: skip the park, by sweeping no more, and the container is
    ///         still there;</item>
    ///         <item>the code commit holding <c>src/Lib/Lib.csproj</c>, and that file holding the package: set the grant's
    ///         <c>AllowedExtensions</c> to <c>[".cs"]</c>, the brief's red, and the <c>.csproj</c> write is refused. The first
    ///         assertion to fail is then the test-result one, because <c>A.cs</c> uses the package and no longer builds;
    ///         with that assertion set aside, the commit assertion fails, missing <c>src/Lib/Lib.csproj</c>;</item>
    ///         <item>no <c>.github/</c> path in the code commit: no Daedalus change can make the sandbox accept the write
    ///         (the default protected paths cannot be removed, ruling R44), and the publish-side check would refuse the
    ///         publish first; it stays as the end-to-end statement of S5's outcome, guarded by the refusal assertion;</item>
    ///         <item>the separate <c>AGENT.md</c> commit: resume without applying the standing instructions, and the newest
    ///         commit is the code commit;</item>
    ///         <item>the Tests section: the recorder red above, with the test-result assertion set aside, and the body says
    ///         no test run was recorded.</item>
    ///     </list>
    /// </remarks>
    [SkippableFact]
    public async Task A_v7_run_writes_a_csproj_in_its_sandbox_parks_at_the_gate_and_publishes_it()
    {
        Skip.IfNot(docker.Available, SandboxImageFixture.SkipReason);

        var chat = new ScriptedChatClient();
        ScriptChangingImplement(chat);
        ScriptReviewAndRetrospect(chat);
        var publisher = new FakePullRequestPublisher();
        await using var host = await StartHostAsync(chat, publisher);
        using var admin = Client(host, "a-admin", "admin");

        var runId = await StartRunAsync(admin);
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate", RunGuard);

        ToolResults(chat, "workspace__write_file").Should().ContainSingle(r => r.Contains($"'{ProtectedPath}' is protected", StringComparison.Ordinal),
            "the sandbox refuses a write under .github/, and only that one");
        var tests = TestResults(await host.RecordsAsync(runId, WorkflowRunRecord.TestResultKind));
        tests.Should().Contain(
            t => string.Equals(t.Tool, "test", StringComparison.Ordinal) && string.Equals(t.Exit, "0", StringComparison.Ordinal) && t.Summary.StartsWith("Passed!", StringComparison.Ordinal),
            "implement's sandbox__test ran the seed's test, passed, and was recorded");

        await SweepUntilNoContainerAsync(host, runId);
        (await docker.RunContainersAsync(host.SandboxNetwork!, runId)).Should().BeEmpty("a run at its gate has its sandbox parked and deleted");

        await ResumeAsync(admin, runId);
        var done = await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Succeeded, "published", PublishGuard);
        done.Variables.Should().ContainKey("pr_url").WhoseValue.Should().Be(FakePullRequestPublisher.Url);

        var commits = host.Remote.Log($"manufacture/{runId}", count: 2);
        commits[0].Files.Should().Equal(["AGENT.md"], "the approved standing instructions are their own commit");
        commits[1].Files.Should().Contain(["src/Lib/Lib.csproj", "src/Lib/A.cs"])
            .And.NotContain(f => f.StartsWith(".github/", StringComparison.Ordinal))
            .And.NotContain("AGENT.md");
        host.Remote.Show(commits[1].Sha, "src/Lib/Lib.csproj").Should().Contain("Newtonsoft.Json", "the code commit holds implement's .csproj, not the seed's");
        publisher.LastBody.Should().Contain("## Tests").And.Contain(PullRequestBody.TestsLabel)
            .And.Contain("- Exit: `0`").And.Contain("- Summary: `Passed!", "the Tests section states the sandbox's own test summary");
    }

    /// <summary>
    ///     S3 in Daedalus: an agent's MSBuild target runs only inside the run's sandbox. Implement writes a
    ///     <c>Directory.Build.props</c> whose target writes <c>marker.txt</c> next to it, has the run's Roslyn server
    ///     evaluate the solution, and runs a test that finds the marker; nothing on this host ever holds the marker.
    /// </summary>
    /// <remarks>
    ///     Reds, per assertion:
    ///     <list type="bullet">
    ///         <item>no <c>marker.txt</c> and no marker text on this host: start the host in local mode with the same
    ///         script and a local grant widened to <c>.props</c>, and the marker appears under <c>DataRoot</c>, written by
    ///         the run's local Roslyn server (done once in review, not kept; see the task B8 report);</item>
    ///         <item>the stored patch holds <c>Directory.Build.props</c>: protect <c>Directory.Build.props</c> through
    ///         <c>Thalos:Workflow:Sandbox:ProtectedPaths</c>, and the write is refused and absent from the patch;</item>
    ///         <item>the marker text is in a <c>sandbox__test</c> result and in no other result: make the target write
    ///         nothing, by turning its <c>WriteLinesToFile</c> into a <c>Message</c>, and no result holds it.</item>
    ///     </list>
    /// </remarks>
    [SkippableFact]
    public async Task An_agent_build_target_never_runs_on_the_api_host()
    {
        Skip.IfNot(docker.Available, SandboxImageFixture.SkipReason);

        var chat = new ScriptedChatClient();
        chat.ThenToolCall("workspace__write_file", new { path = "Directory.Build.props", content = MarkerProps });
        chat.ThenToolCall("workspace__write_file", new { path = "tests/Lib.Tests/MarkerTests.cs", content = MarkerTest });
        chat.ThenToolCall("roslyn__get_diagnostics", new { });
        chat.ThenToolCall("sandbox__test", new { });
        ScriptChangedOutcome(chat, "Added a build marker.", ["Directory.Build.props", "tests/Lib.Tests/MarkerTests.cs"]);
        ScriptReviewAndRetrospect(chat);
        await using var host = await StartHostAsync(chat, new FakePullRequestPublisher());
        using var developer = Client(host, "u-dev", "developer");

        var runId = await StartRunAsync(developer);
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate", RunGuard);
        await SweepUntilNoContainerAsync(host, runId);

        var roots = new[] { host.DataRoot, host.HostToolsRoot!, host.Remote.Url };
        foreach (var root in roots)
        {
            Directory.EnumerateFiles(root, "marker.txt", SearchOption.AllDirectories).Should().BeEmpty($"no MSBuild target of the run may run on this host, under {root}");
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => Holds(f, MarkerText))
                .Should().BeEmpty($"the marker text is only written where the target ran, and nothing under {root} may hold it");
        }

        var patch = Path.Combine(host.DataRoot, "sandboxes", runId.ToString("N") + ".patch");
        File.Exists(patch).Should().BeTrue("parking stores the run's change on the trusted side");
        (await File.ReadAllTextAsync(patch)).Should().Contain("Directory.Build.props", "the agent's props file is part of the run's change");

        var withMarker = AllToolResults(chat).Where(r => r.Result.Contains(MarkerText, StringComparison.Ordinal)).ToList();
        withMarker.Should().NotBeEmpty($"the test run inside the sandbox found the marker its build wrote; the results were: {string.Join(" | ", AllToolResults(chat).Select(r => $"{r.Tool}: {r.Result}"))}");
        withMarker.Should().OnlyContain(r => string.Equals(r.Tool, "sandbox__test", StringComparison.Ordinal), "only a sandbox test run, inside the container, sees the marker");
    }

    /// <summary>
    ///     A publish that fails after the sandbox is gone is retried from the stored patch: the sandbox was deleted when
    ///     the run parked at its gate, and the retry still publishes.
    /// </summary>
    /// <remarks>
    ///     Reds, per assertion:
    ///     <list type="bullet">
    ///         <item>Failed after the resume: let the fake publisher succeed the first time, and the run succeeds;</item>
    ///         <item>no container of the run, after the park and again after the failure: skip the sweep, and the
    ///         container is there;</item>
    ///         <item>the publish succeeding on the retry: remove the stored patch in park, the brief's red. The park is
    ///         inside the pinned Thalos package, so the suite deletes <c>&lt;DataRoot&gt;/sandboxes/&lt;run&gt;.patch</c>
    ///         right after the park instead; the resume is then refused with 422, the patch "is not a regular file", and
    ///         the run never publishes. Deleting it only before the retry changes nothing, because the first publish
    ///         already applied it to the publish worktree, which a retry reuses (task B5).</item>
    ///     </list>
    /// </remarks>
    [SkippableFact]
    public async Task A_retry_after_the_sandbox_is_gone_still_publishes()
    {
        Skip.IfNot(docker.Available, SandboxImageFixture.SkipReason);

        var chat = new ScriptedChatClient();
        ScriptChangingImplement(chat);
        ScriptReviewAndRetrospect(chat);
        var publisher = new FakePullRequestPublisher { FailuresLeft = 1 };
        await using var host = await StartHostAsync(chat, publisher);
        using var admin = Client(host, "a-admin", "admin");

        var runId = await StartRunAsync(admin);
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate", RunGuard);
        await SweepUntilNoContainerAsync(host, runId);
        (await docker.RunContainersAsync(host.SandboxNetwork!, runId)).Should().BeEmpty("the sandbox is deleted when the run parks");

        await ResumeAsync(admin, runId);
        var failed = await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Failed, "failed to open its pull request", PublishGuard);
        failed.CurrentNode.Should().Be("publish");
        (await docker.RunContainersAsync(host.SandboxNetwork!, runId)).Should().BeEmpty("nothing brings the sandbox back for a publish");

        var retry = await admin.PostAsync($"/api/workflow-runs/{runId}/retry", content: null);
        retry.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var done = await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Succeeded, "published on the retry", PublishGuard);
        done.Variables.Should().ContainKey("pr_url").WhoseValue.Should().Be(FakePullRequestPublisher.Url);
        publisher.OpenCount.Should().Be(2, "one failed attempt, then the retry");
        host.Remote.Log($"manufacture/{runId}", count: 2)[1].Files.Should().Contain("src/Lib/A.cs");
    }

    /// <summary>
    ///     A host that stops while a run is at <c>review</c> leaves the run's sandbox running, and a new host on the same
    ///     database and <c>DataRoot</c> carries the run on in that same sandbox: the same container, which still holds
    ///     implement's edit, and no second one.
    /// </summary>
    /// <remarks>
    ///     "The same sandbox id" is held as the same Docker container id: Thalos names a run's sandbox after its run id, so
    ///     the recorded sandbox id is the same by construction even for a sandbox made anew, and only the container id
    ///     tells the two apart.
    ///     Reds, per assertion. The brief's red, having <c>ReconcileAsync</c> delete a sandbox whose record is
    ///     <c>Ready</c>, is inside the pinned Thalos package; it is applied instead as a hosted service on the second host
    ///     that does exactly that at boot, through the registered <c>ISandboxRuntime</c>.
    ///     <list type="bullet">
    ///         <item>the same container, the only run container on the network, when review resumes: that red, and the
    ///         run fails at review with no sandbox, so no review turn starts and the hold is never entered;</item>
    ///         <item>the reviewer reads implement's edit, and Awaiting: the same red, under which the run fails at review
    ///         before either is reached.</item>
    ///     </list>
    /// </remarks>
    [SkippableFact]
    public async Task A_restarted_api_reattaches_to_a_running_sandbox()
    {
        Skip.IfNot(docker.Available, SandboxImageFixture.SkipReason);

        var first = new ScriptedChatClient();
        first.ThenToolCall("workspace__edit_file", new { path = "src/Lib/A.cs", oldText = AddLine, newText = AddLine + "    public static int Twice(int a) => a * 2;\n" });
        ScriptChangedOutcome(first, "Added Twice.", ["src/Lib/A.cs"]);
        // Implement takes three requests: the edit, the outcome and its closing text. The fourth is review's first.
        var heldAtReview = new HoldingChatClient(first, holdAt: 3);
        await using var host = await StartHostAsync(heldAtReview, new FakePullRequestPublisher());
        using var developer = Client(host, "u-dev", "developer");

        var runId = await StartRunAsync(developer);
        await WaitUntilHeldAsync(host, runId, heldAtReview);
        (await host.Store.FindAsync(runId, CancellationToken.None))!.CurrentNode.Should().Be("review");
        var container = (await docker.RunContainersAsync(host.SandboxNetwork!, runId)).Should().ContainSingle().Subject.ID;

        var second = new ScriptedChatClient();
        ScriptReviewAndRetrospect(second, readFirst: "src/Lib/A.cs");
        var heldAgain = new HoldingChatClient(second, holdAt: 0);
        await host.RestartAsync(Services(heldAgain, new FakePullRequestPublisher()));

        await WaitUntilHeldAsync(host, runId, heldAgain);
        var runContainers = await docker.RunContainersAsync(host.SandboxNetwork!);
        runContainers.Should().ContainSingle("no second sandbox is created for the run, and no orphan is left")
            .Which.ID.Should().Be(container, "the new host serves the run from the container the first host created");
        heldAgain.Release();

        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate after the restart", RunGuard);
        ToolResults(second, "workspace__read_file").Should().ContainSingle()
            .Which.Should().Contain("Twice", "the reviewer reads the sandbox implement edited, not a new one from the base commit");
    }

    /// <summary>
    ///     A Docker engine that cannot be reached at start is a 503 with <c>Retry-After</c>, and the start leaves nothing:
    ///     no run row, no sandbox record and no container.
    /// </summary>
    /// <remarks>
    ///     Reds, per assertion: the 503 and its header: drop the <c>Docker:Endpoint</c> override, and the start succeeds
    ///     with 201; no run row: have <c>ManufactureRunStarter</c> start the run even when its workspace could not be
    ///     created; no record: a failed create that kept its record, which only Thalos could do, applied as a record file
    ///     written into <c>&lt;DataRoot&gt;/sandboxes</c> before the start, which proves the check reads where records
    ///     live; no container: the endpoint red with the assertions before it set aside, and the run's containers exist.
    /// </remarks>
    [SkippableFact]
    public async Task A_docker_outage_at_start_is_a_503_and_leaves_nothing()
    {
        Skip.IfNot(docker.Available, SandboxImageFixture.SkipReason);

        var endpoint = OperatingSystem.IsWindows()
            ? "npipe://./pipe/daedalus_b8_no_engine"
            : "unix:///tmp/daedalus-b8-no-engine.sock";
        await using var host = await StartHostAsync(
            new ScriptedChatClient(),
            new FakePullRequestPublisher(),
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Thalos:Workflow:Sandbox:Docker:Endpoint"] = endpoint });
        using var developer = Client(host, "u-dev", "developer");

        var start = await developer.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest("Tighten a guard.", ScratchWorkflowHost.Repository));

        start.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await start.Content.ReadAsStringAsync());
        start.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        (await CountRunsAsync(host)).Should().Be(0, "a start that could not get a sandbox writes no run");
        var records = Path.Combine(host.DataRoot, "sandboxes");
        (Directory.Exists(records) ? Directory.GetFiles(records, "*.json") : []).Should().BeEmpty("a failed create keeps no sandbox record");
        var anything = await docker.Docker.Containers.ListContainersAsync(new Docker.DotNet.Models.ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
            {
                ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal) { [$"{SandboxImageFixture.NetworkLabel}={host.SandboxNetwork}"] = true },
            },
        });
        anything.Should().BeEmpty("nothing was created for the host's network");
    }

    // ---------- the model's scripts ----------

    /// <summary>
    ///     Implement as the brief scripts it: a <c>.csproj</c> that adds Newtonsoft.Json, which the sandbox allows; a
    ///     workflow file under <c>.github/</c>, which it refuses as protected; an edit of <c>A.cs</c> that uses the package;
    ///     a test run; and <c>changed</c>.
    /// </summary>
    private static void ScriptChangingImplement(ScriptedChatClient chat)
    {
        chat.ThenToolCall("workspace__write_file", new { path = "src/Lib/Lib.csproj", content = LibCsprojWithJson });
        chat.ThenToolCall("workspace__write_file", new { path = ProtectedPath, content = "on: push\njobs: {}\n" });
        chat.ThenToolCall("workspace__edit_file", new { path = "src/Lib/A.cs", oldText = AddLine, newText = AddLine + DescribeLine });
        chat.ThenToolCall("sandbox__test", new { });
        ScriptChangedOutcome(chat, "Added A.Describe over Newtonsoft.Json.", ["src/Lib/Lib.csproj", "src/Lib/A.cs"]);
    }

    private static void ScriptChangedOutcome(ScriptedChatClient chat, string summary, string[] files)
    {
        chat.ThenToolCall(OutcomeToolName, new
        {
            outcome = "changed",
            variables = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ReviewHandoff.SummaryKey] = summary,
                [ReviewHandoff.FilesTouchedKey] = files,
                [ReviewHandoff.LearningsKey] = new[] { LearnedLine },
            },
        });
        chat.ThenText("Implemented.");
    }

    /// <summary>
    ///     Each review lens approves with evidence citing the sandbox's test run, the first after reading
    ///     <paramref name="readFirst"/> when given; retrospect proposes the pinned text plus the learning.
    /// </summary>
    private static void ScriptReviewAndRetrospect(ScriptedChatClient chat, string? readFirst = null)
    {
        foreach (var lens in Lenses)
        {
            if (readFirst is not null && string.Equals(lens, Lenses[0], StringComparison.Ordinal))
            {
                chat.ThenToolCall("workspace__read_file", new { path = readFirst });
            }

            chat.ThenToolCall(DaedalusReviewTools.QualifiedReportReviewOutcomeToolName, new
            {
                lens,
                verdict = "approved",
                @checked = $$"""["sandbox__test ran Lib.Tests and reported a passing run ({{lens}})"]""",
            });
            chat.ThenToolCall(OutcomeToolName, new { outcome = "approved" });
            chat.ThenText("Reviewed.");
        }

        chat.ThenToolCall(OutcomeToolName, new
        {
            outcome = ReviewHandoff.RetrospectProposedOutcome,
            variables = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ReviewHandoff.ProposedStandingInstructionsKey] = PinnedStandingInstructions + "\n" + LearnedLine,
            },
        });
        chat.ThenText("Proposed.");
    }

    // ---------- host and helpers ----------

    private Task<ScratchWorkflowHost> StartHostAsync(
        IChatClient chat, FakePullRequestPublisher publisher, IReadOnlyDictionary<string, string?>? settings = null) =>
        ScratchWorkflowHost.StartAsync(
            postgres,
            runtime: null,
            seed: Seed,
            settings: settings,
            configureServices: Services(chat, publisher),
            sandboxImage: docker.ImageTag,
            sandboxNetwork: docker.NewNetwork());

    /// <summary>The scripted model, a fast outbox poll, and one fake as both pull-request interfaces (ruling R28a).</summary>
    private static Action<IServiceCollection> Services(IChatClient chat, FakePullRequestPublisher publisher) => services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IChatClientProvider>(new ScriptedChatClientProvider(chat)));

        services.RemoveAll<WorkflowOutboxDispatchOptions>();
        services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });

        services.RemoveAll<IPullRequestPublisher>();
        services.RemoveAll<IOpenPullRequestLookup>();
        services.AddSingleton<IPullRequestPublisher>(publisher);
        services.AddSingleton<IOpenPullRequestLookup>(publisher);
    };

    /// <summary>A client whose timeout is a hang guard: a start creates the run's sandbox before it answers.</summary>
    private static HttpClient Client(ScratchWorkflowHost host, string user, params string[] roles)
    {
        var client = host.Client(user, roles);
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    private static async Task<Guid> StartRunAsync(HttpClient client)
    {
        var start = await client.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest("Add a Describe helper.", ScratchWorkflowHost.Repository));
        start.StatusCode.Should().Be(HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return (await start.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
    }

    private static async Task ResumeAsync(HttpClient admin, Guid runId)
    {
        var resume = await admin.PostAsJsonAsync($"/api/workflow-runs/{runId}/resume", new ResumeWorkflowRunRequest("human_approval", null, ApplyStandingInstructions: true));
        resume.StatusCode.Should().Be(HttpStatusCode.NoContent, await resume.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     Runs the host's own workspace sweep, at most twice, until the run has no container: the sweep parks the
    ///     sandbox of a run that is not running. Driven here rather than waited for on the host's one-minute timer.
    /// </summary>
    private async Task SweepUntilNoContainerAsync(ScratchWorkflowHost host, Guid runId)
    {
        var sweeper = host.Factory.Services.GetRequiredService<RunWorkspaceSweeper>();
        for (var tick = 0; tick < 2 && (await docker.RunContainersAsync(host.SandboxNetwork!, runId)).Count > 0; tick++)
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await sweeper.SweepAsync(guard.Token);
        }
    }

    /// <summary>
    ///     Waits until <paramref name="held"/> holds a model request of run <paramref name="runId"/>, and fails at once,
    ///     naming where the run stopped, if the run fails or is cancelled first. <see cref="RunGuard"/> is a hang guard.
    /// </summary>
    private static async Task WaitUntilHeldAsync(ScratchWorkflowHost host, Guid runId, HoldingChatClient held)
    {
        var deadline = DateTime.UtcNow + RunGuard;
        WorkflowRun? run = null;
        while (!held.Entered.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            run = await host.Store.FindAsync(runId, CancellationToken.None);
            if (run?.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled)
            {
                break;
            }

            await Task.WhenAny(held.Entered.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
        }

        held.Entered.Task.IsCompleted.Should().BeTrue(
            $"the held model request should have arrived, but the run stopped with status {run?.Status} at '{run?.CurrentNode}', last error: {run?.LastError}");
    }

    private static async Task<long> CountRunsAsync(ScratchWorkflowHost host)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM workflow_run", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static bool Holds(string file, string text)
    {
        try
        {
            return Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(text, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private sealed record TestResult(string Tool, string Exit, string Summary);

    private static List<TestResult> TestResults(IReadOnlyList<WorkflowRunRecord> records) =>
    [
        .. records.Select(r =>
        {
            using var payload = JsonDocument.Parse(r.PayloadJson);
            var root = payload.RootElement;
            return new TestResult(root.GetProperty("tool").GetString()!, root.GetProperty("exit").GetString()!, root.GetProperty("summary").GetString()!);
        }),
    ];

    /// <summary>The result text of every call the model made to <paramref name="toolName"/>, in call order.</summary>
    private static IReadOnlyList<string> ToolResults(ScriptedChatClient chat, string toolName) =>
        [.. AllToolResults(chat).Where(r => string.Equals(r.Tool, toolName, StringComparison.Ordinal)).Select(r => r.Result)];

    /// <summary>
    ///     Every tool call the model made, with the result text it was sent: each call's <see cref="FunctionResultContent"/>
    ///     in a later request, matched by call id.
    /// </summary>
    private static List<(string Tool, string Result)> AllToolResults(ScriptedChatClient chat)
    {
        var contents = chat.Requests.SelectMany(r => r.Messages).SelectMany(m => m.Contents).ToList();
        var calls = contents.OfType<FunctionCallContent>()
            .GroupBy(c => c.CallId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        var results = new List<(string, string)>(calls.Count);
        foreach (var call in calls)
        {
            var result = contents.OfType<FunctionResultContent>().FirstOrDefault(r => string.Equals(r.CallId, call.CallId, StringComparison.Ordinal));
            result.Should().NotBeNull($"the model's '{call.Name}' call '{call.CallId}' must have been answered before the model was asked again");
            results.Add((call.Name, result!.Result is JsonElement json && json.ValueKind == JsonValueKind.String ? json.GetString()! : result.Result?.ToString() ?? ""));
        }

        return results;
    }

    private sealed class ScriptedChatClientProvider(IChatClient client) : IChatClientProvider
    {
        public string Name => "scripted";

        public string DefaultModel => "scripted-model";

        public IChatClient CreateChatClient(AgentDefinition agent) => client;
    }

    /// <summary>
    ///     Passes requests to a script, but holds request number <c>holdAt</c>, counted from zero, until
    ///     <see cref="Release"/> or until the request is cancelled, as a host that stops cancels it. <see cref="Entered"/>
    ///     is set when that request arrives.
    /// </summary>
    private sealed class HoldingChatClient(ScriptedChatClient inner, int holdAt) : IChatClient
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await HoldIfDueAsync(cancellationToken);
            return await inner.GetResponseAsync(messages, options, cancellationToken);
        }

        /// <summary>The host's runtime asks the model through the streaming call, so the hold is here too.</summary>
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await HoldIfDueAsync(cancellationToken);
            await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }

        private async Task HoldIfDueAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) - 1 == holdAt)
            {
                Entered.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

        public void Dispose()
        {
        }
    }
}
