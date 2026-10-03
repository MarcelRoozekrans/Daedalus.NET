using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>What <see cref="SandboxEngineProbe"/> found.</summary>
/// <param name="Available">Whether the suite can run: an engine was found and it runs Linux containers.</param>
/// <param name="SkipReason">Why the suite skips, naming the endpoint it tried; empty when <paramref name="Available"/>.</param>
/// <param name="Engine">The endpoint configuration the probe used, or null when none was found.</param>
internal sealed record SandboxEngine(bool Available, string SkipReason, IDockerEndpointAuthenticationConfiguration? Engine);

/// <summary>
///     Phase 2.6 task B8: whether the sandbox-mode suite can run. It asks the engine Testcontainers itself resolved,
///     <c>TestcontainersSettings.OS.DockerEndpointAuthConfig</c>, which honours <c>DOCKER_HOST</c>, the Testcontainers
///     properties file and the platform defaults in that order, so the probe, the Postgres container, the image build and
///     the sandbox hosts all use one engine: an engine reachable only through <c>DOCKER_HOST</c> runs the suite, and a
///     missing engine or a Windows-container one skips it before anything is started.
/// </summary>
internal static class SandboxEngineProbe
{
    /// <summary>The skip reason when Testcontainers found no engine at all.</summary>
    public const string NoEngine = "no Docker engine found: Testcontainers resolved no endpoint (DOCKER_HOST, ~/.testcontainers.properties or the platform default)";

    /// <summary>Probes the engine Testcontainers resolved.</summary>
    /// <remarks>
    ///     Testcontainers resolves its endpoint in a static initializer, which throws for a malformed <c>DOCKER_HOST</c>,
    ///     such as <c>npipe:////./pipe/...</c>; that is a skip naming the cause, not a failure of the whole collection.
    /// </remarks>
    public static Task<SandboxEngine> ProbeAsync()
    {
        IDockerEndpointAuthenticationConfiguration? engine;
        try
        {
            engine = TestcontainersSettings.OS.DockerEndpointAuthConfig;
        }
        catch (TypeInitializationException ex)
        {
            return Task.FromResult(new SandboxEngine(false, $"Testcontainers could not resolve a Docker endpoint: {ex.InnerException?.Message ?? ex.Message}", null));
        }

        return ProbeAsync(engine, AskOsTypeAsync);
    }

    /// <summary>
    ///     Decides from <paramref name="engine"/>, asking <paramref name="osTypeOf"/> for its <c>OSType</c>. No engine,
    ///     an engine that cannot be asked, and an engine that answers anything but <c>linux</c> each skip with their own
    ///     reason. Never throws.
    /// </summary>
    public static async Task<SandboxEngine> ProbeAsync(
        IDockerEndpointAuthenticationConfiguration? engine,
        Func<IDockerEndpointAuthenticationConfiguration, CancellationToken, Task<string?>> osTypeOf)
    {
        if (engine is null)
        {
            return new SandboxEngine(false, NoEngine, null);
        }

        string? osType;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            osType = await osTypeOf(engine, timeout.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SandboxEngine(false, $"the Docker engine at {engine.Endpoint} could not be asked: {ex.GetType().Name}", engine);
        }

        return string.Equals(osType, "linux", StringComparison.OrdinalIgnoreCase)
            ? new SandboxEngine(true, "", engine)
            : new SandboxEngine(false, $"the Docker engine at {engine.Endpoint} runs '{osType}' containers, and the sandbox images are Linux-only", engine);
    }

    /// <summary>A client for <paramref name="engine"/>, built the way Testcontainers builds its own.</summary>
    public static DockerClient Connect(IDockerEndpointAuthenticationConfiguration engine) =>
        engine.GetDockerClientBuilder().WithTimeout(TimeSpan.FromMinutes(2)).Build();

    private static async Task<string?> AskOsTypeAsync(IDockerEndpointAuthenticationConfiguration engine, CancellationToken ct)
    {
        using var client = Connect(engine);
        return (await client.System.GetSystemInfoAsync(ct)).OSType;
    }
}

/// <summary>
///     Removes the Docker objects the Thalos sandbox runtime made for test hosts, by its <c>thalos.sandbox.network</c>
///     label: containers, with their anonymous volumes, because the egress image declares some; then labelled volumes;
///     then networks. Shared by the B8 fixture and <c>ProcessDefinitionSyncEndToEndTests</c>, whose sandbox-mode host
///     sets up infrastructure on a network of its own. Only objects whose label value a caller's predicate accepts are
///     touched, so a developer's own sandbox network is never reached.
/// </summary>
internal static class SandboxDockerCleanup
{
    /// <summary>The Thalos runtime's label naming the sandbox network an object belongs to.</summary>
    public const string NetworkLabel = "thalos.sandbox.network";

    /// <summary>
    ///     Removes every object of a network <paramref name="ours"/> accepts, created before <paramref name="createdBefore"/>
    ///     when it is set. Best effort per object: one that is already gone or still in use is left and the rest go on.
    /// </summary>
    public static async Task RemoveAsync(DockerClient docker, Func<string, bool> ours, DateTime? createdBefore, CancellationToken ct)
    {
        var cutoff = createdBefore ?? DateTime.MaxValue;
        var labelled = Filter("label", NetworkLabel);

        foreach (var container in await docker.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = labelled }, ct))
        {
            if (container.Labels.TryGetValue(NetworkLabel, out var network) && ours(network) && container.Created.ToUniversalTime() < cutoff)
            {
                await QuietlyAsync(() => docker.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, ct));
            }
        }

        foreach (var volume in (await docker.Volumes.ListAsync(new VolumesListParameters { Filters = labelled }, ct)).Volumes ?? [])
        {
            if (volume.Labels is not null && volume.Labels.TryGetValue(NetworkLabel, out var network) && ours(network) && CreatedBefore(volume.CreatedAt, cutoff))
            {
                await QuietlyAsync(() => docker.Volumes.RemoveAsync(volume.Name, force: true, ct));
            }
        }

        foreach (var network in await docker.Networks.ListNetworksAsync(new NetworksListParameters { Filters = labelled }, ct))
        {
            if (network.Labels is not null && network.Labels.TryGetValue(NetworkLabel, out var name) && ours(name) && network.Created.ToUniversalTime() < cutoff)
            {
                await QuietlyAsync(() => docker.Networks.DeleteNetworkAsync(network.ID, ct));
            }
        }
    }

    /// <summary>
    ///     Removes what the sandbox runtime made for <paramref name="network"/> on the engine Testcontainers resolved, and
    ///     nothing when there is no usable engine. Bounded; a failure is written to standard error, never thrown, so a
    ///     clean-up never hides the test's own outcome.
    /// </summary>
    public static async Task RemoveNetworkScopeAsync(string network)
    {
        if (TestcontainersSettings.OS.DockerEndpointAuthConfig is not { } engine)
        {
            return;
        }

        // Three passes, two seconds apart. A host stopped while its sandbox infrastructure was being set up cancels that
        // set-up client-side, but a create request the engine already received still completes: seen as an egress
        // container in Created state, on the default bridge only, appearing after a first pass had found nothing.
        await BoundedAsync(async ct =>
        {
            using var docker = SandboxEngineProbe.Connect(engine);
            for (var pass = 0; pass < 3; pass++)
            {
                if (pass > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }

                await RemoveAsync(docker, name => string.Equals(name, network, StringComparison.Ordinal), createdBefore: null, ct);
            }
        });
    }

    /// <summary>Runs <paramref name="step"/> under a two-minute bound, writing a failure to standard error instead of throwing.</summary>
    public static async Task BoundedAsync(Func<CancellationToken, Task> step)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await step(timeout.Token);
        }
        catch (Exception ex)
        {
            // Best effort: one failed or timed-out clean-up step must not stop the others or hide a test's outcome.
            await global::System.Console.Error.WriteLineAsync($"Sandbox clean-up step failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Runs <paramref name="action"/>, ignoring an engine error: the object is already gone, or in use by something not ours.</summary>
    public static async Task QuietlyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DockerApiException)
        {
            // Best effort, see the summary.
        }
    }

    public static Dictionary<string, IDictionary<string, bool>> Filter(string key, string value) =>
        new(StringComparer.Ordinal)
        {
            [key] = new Dictionary<string, bool>(StringComparer.Ordinal) { [value] = true },
        };

    private static bool CreatedBefore(string? createdAt, DateTime cutoff) =>
        cutoff == DateTime.MaxValue
        || (DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) && created.UtcDateTime < cutoff);
}
