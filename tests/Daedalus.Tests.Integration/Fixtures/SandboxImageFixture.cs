using System.Diagnostics;
using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Phase 2.6 task B8: the Docker world the sandbox-mode suite shares. Builds the real Daedalus sandbox image,
///     <c>src/Daedalus.Sandbox/Dockerfile</c>, once per test run, tags it <c>daedalus-sandbox:test-&lt;short sha&gt;</c>, and
///     hands out a unique sandbox network name per host, so each host gets its own gateway and egress containers
///     (ruling R45). Everything the suite creates is removed on dispose by label, and leftovers an interrupted earlier
///     run left are removed at start.
/// </summary>
/// <remarks>
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
///     fixture's <see cref="Suffix"/>. The image carries <see cref="ImageLabel"/>. Nothing else on the engine is touched,
///     so a developer's own <c>daedalus-sandboxes</c> network and its containers are left alone.
///     </para>
///     <para>
///     <b>Skips.</b> With no Docker engine running Linux containers, nothing is built and <see cref="Available"/> is
///     false; each test then skips with <see cref="SkipReason"/>, as Thalos's <c>DockerAvailable</c> does.
///     </para>
/// </remarks>
public sealed class SandboxImageFixture : IAsyncLifetime
{
    /// <summary>Every sandbox network this suite hands out starts with this.</summary>
    public const string NetworkPrefix = "daedalus-b8-";

    /// <summary>The label on the image this fixture builds; its value is the fixture's <see cref="Suffix"/>.</summary>
    public const string ImageLabel = "daedalus.tests.sandbox-b8";

    /// <summary>The reason a test skips without a usable engine.</summary>
    public const string SkipReason = "no Docker engine running Linux containers";

    /// <summary>The Thalos runtime's label naming the sandbox network an object belongs to.</summary>
    public const string NetworkLabel = "thalos.sandbox.network";

    /// <summary>The Thalos runtime's label naming the run a sandbox container or volume belongs to.</summary>
    public const string RunIdLabel = "thalos.run_id";

    /// <summary>The Thalos runtime's label marking an object it created.</summary>
    public const string SandboxLabel = "thalos.sandbox";

    /// <summary>Leftovers older than this, from an earlier run that was interrupted, are removed at start.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(3);

    private int _networks;

    /// <summary>Whether a Linux-container Docker engine is there; when false nothing was built.</summary>
    public bool Available { get; private set; }

    /// <summary>Unique to this fixture: it names the networks it hands out and labels the image.</summary>
    public string Suffix { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The built image's tag, <c>daedalus-sandbox:test-&lt;short sha&gt;</c>.</summary>
    public string ImageTag { get; private set; } = "";

    /// <summary>The engine, for tests that inspect what the runtime created. Null when <see cref="Available"/> is false.</summary>
    public DockerClient Docker { get; private set; } = null!;

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
        Available = await ProbeAsync();
        if (!Available)
        {
            return;
        }

        Docker = new DockerClientBuilder().WithTimeout(TimeSpan.FromMinutes(2)).Build();
        await RemoveStaleLeftoversAsync();

        ImageTag = $"daedalus-sandbox:test-{ShortSha()}";
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
    }

    /// <summary>
    ///     Removes every container, volume and network of the networks this fixture handed out, then the image, its
    ///     intermediate layers included. Each step is bounded and isolated, so one failure does not leave the rest.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (!Available)
        {
            return;
        }

        await BoundedAsync(ct => RemoveSandboxObjectsAsync(network => network.StartsWith($"{NetworkPrefix}{Suffix}-", StringComparison.Ordinal), olderThan: null, ct));
        await BoundedAsync(ct => RemoveImagesAsync($"{ImageLabel}={Suffix}", olderThan: null, ct));
        await BoundedAsync(async ct =>
        {
            if (ImageTag.Length > 0)
            {
                await Quietly(() => Docker.Images.DeleteImageAsync(ImageTag, new ImageDeleteParameters { Force = true }, ct));
            }
        });

        Docker.Dispose();
    }

    /// <summary>Removes what an interrupted earlier run of this suite left: anything of ours older than <see cref="StaleAfter"/>.</summary>
    private async Task RemoveStaleLeftoversAsync()
    {
        await BoundedAsync(ct => RemoveSandboxObjectsAsync(network => network.StartsWith(NetworkPrefix, StringComparison.Ordinal), StaleAfter, ct));
        await BoundedAsync(ct => RemoveImagesAsync(ImageLabel, StaleAfter, ct));
    }

    /// <summary>
    ///     Removes the containers, then the volumes, then the networks whose <see cref="NetworkLabel"/> value
    ///     <paramref name="ours"/> accepts, and, when <paramref name="olderThan"/> is set, only those created before then.
    /// </summary>
    private async Task RemoveSandboxObjectsAsync(Func<string, bool> ours, TimeSpan? olderThan, CancellationToken ct)
    {
        var cutoff = olderThan is { } age ? DateTime.UtcNow - age : DateTime.MaxValue;
        var labelled = Filter("label", NetworkLabel);

        foreach (var container in await Docker.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = labelled }, ct))
        {
            if (container.Labels.TryGetValue(NetworkLabel, out var network) && ours(network) && container.Created.ToUniversalTime() < cutoff)
            {
                await Quietly(() => Docker.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, ct));
            }
        }

        foreach (var volume in (await Docker.Volumes.ListAsync(new VolumesListParameters { Filters = labelled }, ct)).Volumes ?? [])
        {
            if (volume.Labels is not null && volume.Labels.TryGetValue(NetworkLabel, out var network) && ours(network) && CreatedBefore(volume.CreatedAt, cutoff))
            {
                await Quietly(() => Docker.Volumes.RemoveAsync(volume.Name, force: true, ct));
            }
        }

        foreach (var network in await Docker.Networks.ListNetworksAsync(new NetworksListParameters { Filters = labelled }, ct))
        {
            if (network.Labels is not null && network.Labels.TryGetValue(NetworkLabel, out var name) && ours(name) && network.Created.ToUniversalTime() < cutoff)
            {
                await Quietly(() => Docker.Networks.DeleteNetworkAsync(network.ID, ct));
            }
        }
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
            var left = (await Docker.Images.ListImagesAsync(new ImagesListParameters { All = true, Filters = Filter("label", label) }, ct))
                .Where(i => i.Created.ToUniversalTime() < cutoff)
                .ToList();
            if (left.Count == 0)
            {
                return;
            }

            foreach (var image in left)
            {
                await Quietly(() => Docker.Images.DeleteImageAsync(image.ID, new ImageDeleteParameters { Force = true }, ct));
            }
        }
    }

    private static bool CreatedBefore(string? createdAt, DateTime cutoff) =>
        cutoff == DateTime.MaxValue
        || (DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) && created.UtcDateTime < cutoff);

    private static Dictionary<string, IDictionary<string, bool>> Filter(string key, string value) =>
        new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
        {
            [key] = new Dictionary<string, bool>(StringComparer.Ordinal) { [value] = true },
        };

    /// <summary>Whether the default engine endpoint exists and runs Linux containers, as Thalos's <c>DockerAvailable</c> asks.</summary>
    private static async Task<bool> ProbeAsync()
    {
        var endpoint = OperatingSystem.IsWindows() ? @"\\.\pipe\docker_engine" : "/var/run/docker.sock";
        if (!File.Exists(endpoint))
        {
            return false;
        }

        try
        {
            using var client = new DockerClientBuilder().WithTimeout(TimeSpan.FromSeconds(10)).Build();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var info = await client.System.GetSystemInfoAsync(timeout.Token);
            return string.Equals(info.OSType, "linux", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
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

    private static async Task BoundedAsync(Func<CancellationToken, Task> step)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await step(timeout.Token);
        }
        catch (Exception ex)
        {
            // Best effort: one failed or timed-out clean-up step must not stop the others.
            await global::System.Console.Error.WriteLineAsync($"Sandbox suite clean-up step failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task Quietly(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DockerApiException)
        {
            // Best effort: already gone, or still in use by something this suite does not own.
        }
    }
}

/// <summary>
///     The B8 tests in one collection: they share one image build and one Postgres container, and run one after
///     another, so builds and sandbox infrastructure never race each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ManufactureSandboxCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<SandboxImageFixture>
{
    public const string Name = "ManufactureSandbox";
}
