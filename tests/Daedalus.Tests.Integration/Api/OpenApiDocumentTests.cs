using System.Text.Json;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Api;

/// <summary>
///     The OpenAPI document reads the Http JsonOptions, whose resolver chain is <c>ApiJsonSerializerContext</c> alone,
///     so every request and response type of a mapped action must be listed there. This guards each later port.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OpenApiDocumentTests(PostgresFixture fixture)
{
    /// <summary>
    ///     Red: remove one listed type, for example <c>IReadOnlyList of AgentSummaryDto</c>, from the context; the
    ///     document answers 500 with "JsonTypeInfo metadata ... was not provided".
    /// </summary>
    [Fact]
    public async Task The_openapi_document_is_served_in_development()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());
        // The base factory sets Development as well; naming it here keeps the test from turning into a 404 if
        // the factory's environment ever changes, since MapOpenApi is Development-only.
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("paths").EnumerateObject().Should().NotBeEmpty();
    }

    /// <summary>
    ///     Red: a later <c>Configure of JsonOptions</c> that appends a resolver, or drops the context, from the Http
    ///     options' chain leaves more or fewer than the context alone; the reflection fallback is the case it guards.
    /// </summary>
    [Fact]
    public async Task The_real_host_resolves_endpoint_json_through_the_context_alone()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());

        var options = factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

        options.TypeInfoResolverChain.Should().ContainSingle()
            .Which.GetType().FullName.Should().Be("Daedalus.Api.ApiJsonSerializerContext");
    }

    /// <summary>
    ///     A <c>BindAsync</c> parameter contributes no OpenAPI parameter, so a port re-declares its query parameters (R30).
    ///     MVC described <c>take</c> on the run history as an integer-or-string query parameter with default 20.
    ///     Red: drop the <c>AddOpenApiOperationTransformer</c> from the run-history route in
    ///     <c>SchedulesEndpoints</c>; the parameter is missing, and a changed default of 20 turns the default red.
    /// </summary>
    [Fact]
    public async Task The_run_history_declares_take_with_its_default()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        var take = document.RootElement.GetProperty("paths").GetProperty("/api/schedules/{scheduleId}/runs")
            .GetProperty("get").GetProperty("parameters").EnumerateArray()
            .Single(parameter => string.Equals(parameter.GetProperty("name").GetString(), "take", StringComparison.Ordinal));
        take.GetProperty("in").GetString().Should().Be("query");
        take.GetProperty("schema").GetProperty("default").GetInt32().Should().Be(20);
        take.GetProperty("schema").GetProperty("format").GetString().Should().Be("int32");
    }
}
