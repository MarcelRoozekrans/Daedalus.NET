using Daedalus.Infrastructure.Configuration;

namespace Daedalus.Tests.Unit.Infrastructure.Configuration;

public sealed class OllamaConnectionStringTests
{
    /// <summary>Red: stop stripping the Endpoint= prefix before the URL is parsed; the URI is wrong or the parse throws.</summary>
    [Fact]
    public void Endpoint_form_parses()
    {
        OllamaConnectionString.ParseEndpoint("Endpoint=http://localhost:11434").Should().Be(new Uri("http://localhost:11434"));
    }

    /// <summary>Red: return the Model value instead of the Endpoint value; the URI is wrong or the parse throws.</summary>
    [Fact]
    public void Endpoint_with_model_parses_to_the_endpoint()
    {
        OllamaConnectionString.ParseEndpoint("Endpoint=http://ollama:11434;Model=nomic-embed-text")
            .Should().Be(new Uri("http://ollama:11434"));
    }

    /// <summary>Red: refuse any string that does not start with Endpoint=; this throws FormatException.</summary>
    [Fact]
    public void A_bare_url_parses()
    {
        OllamaConnectionString.ParseEndpoint("http://localhost:11434").Should().Be(new Uri("http://localhost:11434"));
    }

    /// <summary>Red: return an arbitrary default URI for input without an endpoint instead of throwing; the exception is not thrown.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("Model=nomic-embed-text")]
    [InlineData("Endpoint=")]
    [InlineData("Endpoint=/relative")]
    public void Anything_else_throws_FormatException_naming_ConnectionStrings_ollama(string connectionString)
    {
        var act = () => OllamaConnectionString.ParseEndpoint(connectionString);

        act.Should().Throw<FormatException>().Which.Message.Should().Contain("ConnectionStrings:ollama");
    }
}
