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
    // The caller owns the app and resolves everything it needs inside the app's lifetime.
    private static async Task<DistributedApplication> BuildAppAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHostAssembly::Program>(
            ["--Parameters:db-username=u", "--Parameters:db-password=p", "--Parameters:anthropic-api-key=k"]);
        return await builder.BuildAsync();
    }

    private static DistributedApplicationModel ModelOf(DistributedApplication app) =>
        (DistributedApplicationModel)app.Services.GetService(typeof(DistributedApplicationModel))!;

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
        await using var app = await BuildAppAsync();
        var model = ModelOf(app);
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
        await using var app = await BuildAppAsync();
        var model = ModelOf(app);
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
        await using var app = await BuildAppAsync();
        var model = ModelOf(app);
        var pull = Find<ContainerResource>(model, "ollama-pull");

        var resolved = await ResolveAsync(pull);
        var environment = resolved.EnvironmentVariables.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var args = resolved.Arguments.Select(a => a.Value).ToList();

        // Red: OLLAMA_HOST taken from another endpoint, or none, so the pull talks to a default localhost server.
        environment.Should().ContainKey("OLLAMA_HOST").WhoseValue.Should().Be("{ollama.bindings.http.url}");
        // Red: pulling any other model, or none.
        args.Should().ContainSingle(a => a.Contains($"ollama pull {ModelName}", StringComparison.Ordinal));
        // Red: dropping the show-or-pull guard, `ollama show <model> || ollama pull <model>`, so an offline restart
        // fails even though the model is already in the volume.
        args.Should().ContainSingle(a => a.Contains($"ollama show {ModelName} >/dev/null 2>&1 || ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keycloak_is_kept_on_plain_http_instead_of_the_developer_certificate()
    {
        await using var app = await BuildAppAsync();
        var model = ModelOf(app);
        var keycloak = Find<IResource>(model, "daedalus-realm");

        // The Api, the Web client and the README all use http://localhost:8082. With a valid ASP.NET developer
        // certificate on the machine Aspire otherwise terminates TLS on that port, so the http URL gets an empty reply.
        // Red: dropping .WithoutHttpsCertificate() from the Keycloak builder. No HttpsCertificateAnnotation is then
        // present, the Aspire default applies, and the developer certificate is injected.
#pragma warning disable ASPIRECERTIFICATES001 // Experimental Aspire certificate API, the same opt-in the AppHost makes.
        var annotation = keycloak.Annotations.OfType<HttpsCertificateAnnotation>().Should().ContainSingle().Subject;
        // Red: replacing it with WithHttpsCertificate or WithDeveloperCertificate style opt-in.
        annotation.UseDeveloperCertificate.Should().BeFalse();
        annotation.Certificate.Should().BeNull();
#pragma warning restore ASPIRECERTIFICATES001
    }
}
