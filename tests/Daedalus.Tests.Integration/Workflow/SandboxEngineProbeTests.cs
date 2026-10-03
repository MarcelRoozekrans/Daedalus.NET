using Daedalus.Tests.Integration.Fixtures;
using DotNet.Testcontainers.Configurations;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.6 task B8, fix round 1: the sandbox suite's engine probe decides on the endpoint it is given, the one
///     Testcontainers resolved, and skips for each way an engine can be unusable. These need no engine: the engine's
///     answer is supplied. The no-engine path was also run for real; see the task B8 report.
/// </summary>
public sealed class SandboxEngineProbeTests
{
    /// <summary>Red: probe a fixed default endpoint, or return available, when no endpoint was resolved.</summary>
    [Fact]
    public async Task No_resolved_engine_skips_without_asking_anything()
    {
        var asked = false;

        var probe = await SandboxEngineProbe.ProbeAsync(null, (_, _) =>
        {
            asked = true;
            return Task.FromResult<string?>("linux");
        });

        probe.Available.Should().BeFalse();
        probe.SkipReason.Should().Be(SandboxEngineProbe.NoEngine);
        asked.Should().BeFalse("there is no engine to ask");
    }

    /// <summary>Red: accept any answer as Linux, or treat the answer case-sensitively in the other direction.</summary>
    [Fact]
    public async Task An_engine_running_windows_containers_skips_and_says_so()
    {
        var probe = await SandboxEngineProbe.ProbeAsync(Engine("npipe://./pipe/docker_engine"), (_, _) => Task.FromResult<string?>("windows"));

        probe.Available.Should().BeFalse();
        probe.SkipReason.Should().Contain("'windows' containers").And.Contain("npipe://./pipe/docker_engine");
    }

    /// <summary>Red: let the engine's exception escape the probe, which would fail the whole collection instead of skipping.</summary>
    [Fact]
    public async Task An_engine_that_cannot_be_asked_skips_and_names_the_endpoint()
    {
        var probe = await SandboxEngineProbe.ProbeAsync(Engine("unix:///var/run/docker.sock"), (_, _) => throw new HttpRequestException("connection refused"));

        probe.Available.Should().BeFalse();
        probe.SkipReason.Should().Contain("unix:///var/run/docker.sock").And.Contain(nameof(HttpRequestException));
    }

    /// <summary>
    ///     A <c>DOCKER_HOST</c>-only engine: the probe asks the endpoint it was given, not the platform default, and a Linux
    ///     answer makes the suite run on that endpoint. Red: probe the platform default's file, as the first version did,
    ///     which skips this engine.
    /// </summary>
    [Fact]
    public async Task An_engine_reached_only_through_its_resolved_endpoint_runs_the_suite_on_that_endpoint()
    {
        var resolved = Engine("tcp://docker.example.invalid:2375");
        Uri? asked = null;

        var probe = await SandboxEngineProbe.ProbeAsync(resolved, (engine, _) =>
        {
            asked = engine.Endpoint;
            return Task.FromResult<string?>("Linux");
        });

        probe.Available.Should().BeTrue();
        asked.Should().Be(resolved.Endpoint);
        probe.Engine!.Endpoint.Should().Be(resolved.Endpoint);
    }

    private static DockerEndpointAuthenticationConfiguration Engine(string endpoint) => new(new Uri(endpoint));
}
