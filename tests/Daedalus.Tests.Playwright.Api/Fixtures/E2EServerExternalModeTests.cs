using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Daedalus.Tests.Playwright.Api;

/// <summary>
///     The suite's external mode, the one CI uses to run it against the natively published
///     <c>Daedalus.Tests.NativeApiHost</c>: with <c>E2E_BASE_URL</c> set, the fixture serves the tests from that URL
///     and starts neither a database container nor an in-process host.
/// </summary>
[TestFixture]
public sealed class E2EServerExternalModeTests
{
    private const string FakeHealthBody = "served by the external host";

    /// <summary>
    ///     Red: drop the <c>E2E_BASE_URL</c> branch from <c>E2EServer.StartAsync</c>; the server starts its own
    ///     container and in-process host, and its client reaches that host instead of the external one.
    /// </summary>
    [Test]
    public async Task Fixture_in_external_mode_does_not_start_an_in_process_host()
    {
        // A stand-in for the native host: any server on a real socket whose health answer the in-process Api cannot give.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var external = builder.Build();
        external.MapGet("/health", () => Results.Text(FakeHealthBody));
        await external.StartAsync().ConfigureAwait(false);
        var externalUrl = new Uri(external.Urls.Single());

        // The suite's own database, already migrated and seeded by the fixture this test runs under.
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [E2EServer.BaseUrlVariable] = externalUrl.ToString(),
            [E2EServer.ConnectionStringVariable] = E2EServerFixture.ConnectionString,
        };

        await using var server = new E2EServer(name => variables.GetValueOrDefault(name));
        await server.StartAsync().ConfigureAwait(false);

        server.HostsInProcess.Should().BeFalse("an external host serves the suite, so none starts in this process");
        server.OwnsDatabaseContainer.Should().BeFalse("the external host's database is named by E2E_CONNECTION_STRING");
        server.ServerUrl.Should().Be(externalUrl);
        server.ConnectionString.Should().Be(E2EServerFixture.ConnectionString);
        server.IsReady.Should().BeTrue();

        using var client = server.CreateClient();
        var health = await client.GetStringAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        health.Should().Be(FakeHealthBody, "the client must reach the external host, not an in-memory one");
    }

    /// <summary>
    ///     Red: drop the check for <c>E2E_CONNECTION_STRING</c> without <c>E2E_BASE_URL</c> from
    ///     <c>E2EServer.StartAsync</c>; the server ignores the database it was given and starts its own host.
    /// </summary>
    [Test]
    public async Task Fixture_with_a_connection_string_but_no_base_url_refuses_to_start()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [E2EServer.ConnectionStringVariable] = E2EServerFixture.ConnectionString,
        };

        await using var server = new E2EServer(name => variables.GetValueOrDefault(name));
        var start = server.StartAsync;

        (await start.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false))
            .WithMessage($"*{E2EServer.BaseUrlVariable}*");
        server.HostsInProcess.Should().BeFalse();
        server.OwnsDatabaseContainer.Should().BeFalse();
    }

    /// <summary>
    ///     Red: let external mode swallow a failed seed, as in-process mode does; setup then succeeds over a database
    ///     the suite's seed never reached.
    /// </summary>
    [Test]
    public async Task Fixture_in_external_mode_fails_when_the_seed_fails()
    {
        // A database that does not exist on the suite's server: the connection, and so the seed, fails.
        var missingDatabase = new Npgsql.NpgsqlConnectionStringBuilder(E2EServerFixture.ConnectionString)
        {
            Database = "no_such_database_for_the_seed",
        }.ConnectionString;
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [E2EServer.BaseUrlVariable] = "http://127.0.0.1:9/",
            [E2EServer.ConnectionStringVariable] = missingDatabase,
        };

        await using var server = new E2EServer(name => variables.GetValueOrDefault(name));
        var start = server.StartAsync;

        await start.Should().ThrowAsync<System.Data.Common.DbException>().ConfigureAwait(false);
        server.IsReady.Should().BeFalse();
    }
}
