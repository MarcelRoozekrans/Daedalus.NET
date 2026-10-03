using System.Diagnostics;
using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Phase 2.6 task B8: the Docker world the sandbox-mode suite shares, and the suite's only collection fixture. It
///     first probes the engine Testcontainers resolves (<see cref="SandboxEngineProbe"/>); only when that engine runs
///     Linux containers does it start the suite's Postgres container, build the real Daedalus sandbox image,
///     <c>src/Daedalus.Sandbox/Dockerfile</c>, once per test run, and hand out a unique sandbox network name per host, so
///     each host gets its own gateway and egress containers (ruling R45). Everything the suite creates is removed on
///     dispose by label, and leftovers an interrupted earlier run left are removed at start.
/// </summary>
/// <remarks>
///     <para>
///     <b>Skips.</b> With no engine, or an engine running Windows containers, nothing is started, not even Postgres, and
///     <see cref="Available"/> is false; each test then skips with <see cref="SkipReason"/>, which names the endpoint
///     tried. The engine is the one Testcontainers resolves, <c>DOCKER_HOST</c> included, and the sandbox hosts are pointed
///     at the same endpoint through <see cref="EngineEndpoint"/>, so a probe, a Postgres container and a sandbox never
///     reach two different engines.
///     </para>
///     <para>
///     <b>Build context.</b> The repository root, filtered by <c>src/Daedalus.Sandbox/Dockerfile.dockerignore</c>, exactly
///     as <c>docker build -f src/Daedalus.Sandbox/Dockerfile .</c> sends it. Testcontainers 4.14.0's
///     <c>DockerfileArchive</c> reads <c>&lt;Dockerfile&gt;.dockerignore</c> from the Dockerfile's directory and applies it to
///     the context directory, so the context sent is the allow-list: <c>Directory.Build.props</c>,
///     <c>Directory.Packages.props</c>, <c>nuget.config</c>, <c>.editorconfig</c> and <c>src/Daedalus.Sandbox</c> without
///     <c>bin</c> and <c>obj</c> (checked once against <c>IgnoreFile</c> while writing this fixture; see the task B8
///     report). The Dockerfile itself is sent at the archive's root, which is where Testcontainers points the build.
///     </para>
///     <para>
///     <b>What is ours.</b> Every Docker object the sandbox runtime creates carries <c>thalos.sandbox.network</c> with the
///     host's network name, and every network handed out here starts with <see cref="NetworkPrefix"/> and this
///     fixture's <see cref="Suffix"/>. The image's tag holds the suffix too, and the image carries <see cref="ImageLabel"/>,
///     so two runs on one commit never remove each other's image. Nothing else on the engine is touched, so a developer's
///     own <c>daedalus-sandboxes</c> network and its containers are left alone.
///     </para>
/// </remarks>
public sealed class SandboxImageFixture : IAsyncLifetime
{
    /// <summary>Every sandbox network this suite hands out starts with this.</summary>
    public const string NetworkPrefix = "daedalus-b8-";

    /// <summary>The label on the image this fixture builds; its value is the fixture's <see cref="Suffix"/>.</summary>
    public const string ImageLabel = "daedalus.tests.sandbox-b8";

    /// <summary>The Thalos runtime's label naming the sandbox network an object belongs to.</summary>
    public const string NetworkLabel = SandboxDockerCleanup.NetworkLabel;

    /// <summary>The Thalos runtime's label naming the run a sandbox container or volume belongs to.</summary>
    public const string RunIdLabel = "thalos.run_id";

    /// <summary>Leftovers older than this, from an earlier run that was interrupted, are removed at start.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(3);

    private int _networks;
    private PostgresFixture? _postgres;

    /// <summary>Whether a Linux-container Docker engine is there; when false nothing was started or built.</summary>
    public bool Available { get; private set; }

    /// <summary>Why the suite skips, naming the endpoint the probe tried; empty when <see cref="Available"/>.</summary>
    public string SkipReason { get; private set; } = SandboxEngineProbe.NoEngine;

    /// <summary>The engine's endpoint, as <c>Thalos:Workflow:Sandbox:Docker:Endpoint</c> takes it.</summary>
    public string EngineEndpoint { get; private set; } = "";

    /// <summary>Unique to this fixture: it names the networks it hands out, the image's tag and its label.</summary>
    public string Suffix { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The built image's tag, <c>daedalus-sandbox:test-&lt;short sha&gt;-&lt;suffix&gt;</c>.</summary>
    public string ImageTag { get; private set; } = "";

    /// <summary>The engine, for tests that inspect what the runtime created. Null when <see cref="Available"/> is false.</summary>
    public DockerClient Docker { get; private set; } = null!;

    /// <summary>The suite's Postgres, started only once the probe passed.</summary>
    public PostgresFixture Postgres => _postgres ?? throw new InvalidOperationException($"No Postgres: {SkipReason}");

    /// <summary>The repository root, the directory holding <c>Daedalus.sln</c>.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>A sandbox network name no other host of this run uses.</summary>
    public string NewNetwork() =>
        $"{NetworkPrefix}{Suffix}-{Interlocked.Increment(ref _networks).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    ///     The run containers of <paramref name="network"/>, optionally only <paramref name="runId"/>'s, stopped ones
    ///     included: what the runtime created for runs, never the gateway or the egress proxy.
    /// </summary>
    public async Task<IList<ContainerListResponse>> RunContainersAsync(string network, Guid? runId = null)
    {
        var labels = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [$"{NetworkLabel}={network}"] = true,
            ["thalos.sandbox.role=run"] = true,
        };
        if (runId is { } id)
        {
            labels[$"{RunIdLabel}={id:D}"] = true;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        return await Docker.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal) { ["label"] = labels },
            },
            timeout.Token);
    }

    public async Task InitializeAsync()
    {
        var probe = await SandboxEngineProbe.ProbeAsync();
        SkipReason = probe.SkipReason;
        if (!probe.Available)
        {
            return;
        }

        EngineEndpoint = probe.Engine!.Endpoint.ToString();
        Docker = SandboxEngineProbe.Connect(probe.Engine);
        await RemoveStaleLeftoversAsync();

        _postgres = new PostgresFixture();
        await _postgres.InitializeAsync();

        ImageTag = $"daedalus-sandbox:test-{ShortSha()}-{Suffix}";
        var image = new ImageFromDockerfileBuilder()
            .WithContextDirectory(RepositoryRoot)
            .WithDockerfileDirectory(Path.Combine(RepositoryRoot, "src", "Daedalus.Sandbox"))
            .WithDockerfile("Dockerfile")
            .WithName(ImageTag)
            .WithLabel(ImageLabel, Suffix)
            .WithDeleteIfExists(true)
            // Removed by this fixture's DisposeAsync, by tag and label, not by Testcontainers' reaper: the reaper would
            // also take it while a slower test class in the same run still needs it.
            .WithCleanUp(false)
            .Build();

        // A generous hang guard, not a time assertion: a cold build pulls the .NET SDK image twice over.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        await image.CreateAsync(guard.Token);
        Available = true;
    }

    /// <summary>
    ///     Removes every container, volume and network of the networks this fixture handed out, then the image, its
    ///     intermediate layers included, then stops Postgres. Each step is bounded and isolated, so one failure does not
    ///     leave the rest.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (Docker is not null)
        {
            await SandboxDockerCleanup.BoundedAsync(ct => SandboxDockerCleanup.RemoveAsync(
                Docker, network => network.StartsWith($"{NetworkPrefix}{Suffix}-", StringComparison.Ordinal), createdBefore: null, ct));
            await SandboxDockerCleanup.BoundedAsync(ct => RemoveImagesAsync($"{ImageLabel}={Suffix}", olderThan: null, ct));
            await SandboxDockerCleanup.BoundedAsync(async ct =>
            {
                if (ImageTag.Length > 0)
                {
                    await SandboxDockerCleanup.QuietlyAsync(() => Docker.Images.DeleteImageAsync(ImageTag, new ImageDeleteParameters { Force = true }, ct));
                }
            });
            Docker.Dispose();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>Removes what an interrupted earlier run of this suite left: anything of ours older than <see cref="StaleAfter"/>.</summary>
    private async Task RemoveStaleLeftoversAsync()
    {
        await SandboxDockerCleanup.BoundedAsync(ct => SandboxDockerCleanup.RemoveAsync(
            Docker, network => network.StartsWith(NetworkPrefix, StringComparison.Ordinal), DateTime.UtcNow - StaleAfter, ct));
        await SandboxDockerCleanup.BoundedAsync(ct => RemoveImagesAsync(ImageLabel, StaleAfter, ct));
    }

    /// <summary>
    ///     Removes the images carrying <paramref name="label"/> (a key, or <c>key=value</c>), intermediate ones included, in
    ///     a few passes because an image is removable only once the images built on it are gone.
    /// </summary>
    private async Task RemoveImagesAsync(string label, TimeSpan? olderThan, CancellationToken ct)
    {
        var cutoff = olderThan is { } age ? DateTime.UtcNow - age : DateTime.MaxValue;
        for (var pass = 0; pass < 5; pass++)
        {
            var left = (await Docker.Images.ListImagesAsync(new ImagesListParameters { All = true, Filters = SandboxDockerCleanup.Filter("label", label) }, ct))
                .Where(i => i.Created.ToUniversalTime() < cutoff)
                .ToList();
            if (left.Count == 0)
            {
                return;
            }

            foreach (var image in left)
            {
                await SandboxDockerCleanup.QuietlyAsync(() => Docker.Images.DeleteImageAsync(image.ID, new ImageDeleteParameters { Force = true }, ct));
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Daedalus.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No Daedalus.sln above '{AppContext.BaseDirectory}'.");
    }

    private static string ShortSha()
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("--short=10");
        startInfo.ArgumentList.Add("HEAD");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var sha = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && sha.Length > 0 ? sha : "unknown";
    }
}

/// <summary>
///     The B8 tests in one collection: they share one engine probe, one Postgres container and one image build, and run
///     one after another, so builds and sandbox infrastructure never race each other. <see cref="SandboxImageFixture"/> is
///     the only collection fixture, so nothing starts before its probe has decided the suite can run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ManufactureSandboxCollection : ICollectionFixture<SandboxImageFixture>
{
    public const string Name = "ManufactureSandbox";
}
