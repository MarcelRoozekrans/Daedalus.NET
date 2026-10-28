extern alias AppHostAssembly;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Integration.AppHost;

/// <summary>
///     Pins how the AppHost wires Ollama for the embedding path (#412). The app model is built with
///     <see cref="DistributedApplicationTestingBuilder" /> and nothing is started: no container, no process.
/// </summary>
public sealed class AppHostOllamaWiringTests
{
    private const string ModelName = "nomic-embed-text";

    // The testing builder reads the AppHost's parameters from configuration and never resolves them, but the model
    // builds only when the same three values the AppHost declares exist as values, not as missing parameters.
    private static async Task<DistributedApplicationModel> BuildModelAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHostAssembly::Program>(
            ["--Parameters:db-username=u", "--Parameters:db-password=p", "--Parameters:anthropic-api-key=k"]);
        await using var app = await builder.BuildAsync();
        return (DistributedApplicationModel)app.Services.GetService(typeof(DistributedApplicationModel))!;
    }

    // Publish mode renders every reference as its manifest expression, such as {ollama.bindings.http.url}, so no
    // endpoint needs an allocated port and nothing needs to run.
    private static async Task<IExecutionConfigurationResult> ResolveAsync(IResource resource) =>
        await ExecutionConfigurationBuilder.Create(resource)
            .WithArgumentsConfig()
            .WithEnvironmentVariablesConfig()
            .BuildAsync(
                new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
                NullLogger.Instance,
                CancellationToken.None);

    private static T Find<T>(DistributedApplicationModel model, string name)
        where T : IResource =>
        model.Resources.OfType<T>().Single(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    [Fact]
    public async Task The_api_receives_ConnectionStrings_ollama_as_an_endpoint()
    {
        var model = await BuildModelAsync();
        var api = Find<IResourceWithEnvironment>(model, "api");

        var environment = (await ResolveAsync(api)).EnvironmentVariables.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

        // Red: the old AddOllama().AddModel wiring. It hands the api a connection string for the model resource,
        // ConnectionStrings__ollama-nomic-embed-text, so ConnectionStrings__ollama is absent (#412).
        environment.Should().ContainKey("ConnectionStrings__ollama")
            .WhoseValue.Should().Be("Endpoint={ollama.bindings.http.url}");
    }

    [Fact]
    public async Task The_api_waits_for_the_model_pull_to_complete()
    {
        var model = await BuildModelAsync();
        var api = Find<IResourceWithWaitSupport>(model, "api");
        var pull = Find<IResource>(model, "ollama-pull");

        // Red: dropping .WaitForCompletion(ollamaPull) from the api, or using .WaitFor, which waits for the pull to
        // be running rather than finished.
        api.Annotations.OfType<WaitAnnotation>()
            .Should().Contain(w => ReferenceEquals(w.Resource, pull) && w.WaitType == WaitType.WaitForCompletion);
    }

    [Fact]
    public async Task The_pull_targets_the_ollama_server_and_nomic_embed_text()
    {
        var model = await BuildModelAsync();
        var pull = Find<ContainerResource>(model, "ollama-pull");

        var resolved = await ResolveAsync(pull);
        var environment = resolved.EnvironmentVariables.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var args = resolved.Arguments.Select(a => a.Value).ToList();

        // Red: OLLAMA_HOST taken from another endpoint, or none, so the pull talks to a default localhost server.
        environment.Should().ContainKey("OLLAMA_HOST").WhoseValue.Should().Be("{ollama.bindings.http.url}");
        // Red: pulling any other model, or none.
        args.Should().ContainSingle(a => a.Contains($"ollama pull {ModelName}", StringComparison.Ordinal));
    }
}
