using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Daedalus.Web.State;
using Daedalus.Web.State.Runs;
using Daedalus.Web.State.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The run hub connection: its address, who it opens for, and that a failed start is a status, not an exception.</summary>
public sealed class RunHubConnectionTests : IDisposable
{
    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly RecordingDispatcher _dispatcher = new();

    public void Dispose()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    /// <summary>
    ///     The hub sits at the API's origin, as every ApiClient route does, whatever path or trailing slash the base has.
    ///     Red: resolve the hub path segment-relative, without its leading slash.
    /// </summary>
    [Theory]
    [InlineData("http://h:5010")]
    [InlineData("http://h:5010/")]
    [InlineData("http://h:5010/app/")]
    public void The_hub_resolves_like_the_api_routes(string apiBase)
    {
        var baseUri = new Uri(apiBase);

        RunHubConnection.HubUri(baseUri).AbsoluteUri.Should().Be(
            new Uri(baseUri, "/hubs/workflow-runs").AbsoluteUri,
            "the hub resolves against the base exactly as the API's root-relative routes do")
            .And.Be("http://h:5010/hubs/workflow-runs");
    }

    /// <summary>
    ///     A user who may not view runs gets no connection, so nothing is dispatched.
    ///     Red: drop the CanView check.
    /// </summary>
    [Fact]
    public async Task A_user_who_may_not_view_runs_gets_no_connection()
    {
        await using var hub = Hub(User("task-manager"));

        await hub.EnsureStartedAsync(CancellationToken.None);

        _dispatcher.Actions.Should().BeEmpty();
    }

    /// <summary>
    ///     A start that cannot reach the hub is one Disconnected status and no exception; a second call does not retry.
    ///     Red: narrow the catch filter to HttpRequestException. Red: forget the connection after a failed start.
    /// </summary>
    [Fact]
    public async Task A_failed_start_is_disconnected_not_an_exception()
    {
        await using var hub = Hub(User("developer"));

        await hub.EnsureStartedAsync(CancellationToken.None);
        await hub.EnsureStartedAsync(CancellationToken.None);

        _dispatcher.Actions.Should().Equal(new HubStatusChanged(HubStatus.Disconnected));
    }

    private RunHubConnection Hub(ClaimsPrincipal user)
    {
        var dispatcher = _provider.GetRequiredService<IDispatcher>();
        var api = _handler.Client();
        return new RunHubConnection(
            new ApiBaseAddress(new Uri($"http://127.0.0.1:{RefusedPort()}")),
            new FixedAuthentication(user),
            _provider,
            _dispatcher,
            new RunsEffects(api, dispatcher, _provider.GetRequiredService<IStore<RunsState>>()),
            new TasksEffects(api, dispatcher, _provider.GetRequiredService<IStore<TasksState>>()));
    }

    private static ClaimsPrincipal User(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

    /// <summary>A loopback port nothing listens on: bound, read and released, so a connect is refused at once.</summary>
    private static int RefusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FixedAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private sealed class RecordingDispatcher : IDispatcher
    {
        public List<object> Actions { get; } = [];

        public ValueTask DispatchAsync<TAction>(TAction action)
        {
            Actions.Add(action!);
            return ValueTask.CompletedTask;
        }
    }
}
