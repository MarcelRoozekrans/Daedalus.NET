using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Api;

/// <summary>Phase 3.3: the API host's embedding generator is Thalos.NET.Ollama's, built from the AppHost's connection string shape.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ApiHostEmbeddingsTests(PostgresFixture fixture)
{
    /// <summary>Red: register nothing in Program.cs, or pass another model or URI to UseOllamaEmbeddings; the metadata differs.</summary>
    [Fact]
    public async Task The_api_host_registers_the_ollama_generator_from_the_apphost_shape()
    {
        await using var factory = new ApiWebApplicationFactory(
            fixture.ConnectionString, Substitute.For<IAgentRuntime>(), settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:ollama"] = "Endpoint=http://localhost:11434",
            });

        var generator = factory.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();
        metadata.Should().NotBeNull();
        metadata!.ProviderName.Should().Be("ollama");
        metadata.ProviderUri.Should().Be(new Uri("http://localhost:11434/"));
        metadata.DefaultModelId.Should().Be("nomic-embed-text");
    }

    /// <summary>Red: register the generator unconditionally in Program.cs; the host then resolves one.</summary>
    [Fact]
    public async Task Without_a_connection_string_no_generator_is_registered()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());

        factory.Services.GetService<IEmbeddingGenerator<string, Embedding<float>>>().Should().BeNull();
    }
}
