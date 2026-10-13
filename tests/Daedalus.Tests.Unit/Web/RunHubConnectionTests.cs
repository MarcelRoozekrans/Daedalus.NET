using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Claims;
using Daedalus.Web.State;
using Daedalus.Web.State.Runs;
using Daedalus.Web.State.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
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
    ///     A start that cannot reach the hub is one Disconnected status and no exception.
    ///     Red: narrow the catch filter to InvalidOperationException: the refused connect escapes.
    ///     Red: drop the Disconnected dispatch from a failed start: no action is recorded.
    /// </summary>
    [Fact]
    public async Task A_failed_start_is_disconnected_not_an_exception()
    {
        await using var hub = Hub(User("developer"));

        await hub.EnsureStartedAsync(CancellationToken.None);

        _dispatcher.Actions.Should().Equal(new HubStatusChanged(HubStatus.Disconnected));
    }

    /// <summary>
    ///     A start that fails with something other than a refused connect, a handshake timeout, a cancellation the
    ///     caller did not ask for, a hub error or an unreadable handshake, is a Disconnected status too, never an
    ///     exception that would reach the layout.
    ///     Red: restore the narrow filter, HttpRequestException, WebSocketException or InvalidOperationException:
    ///     each of these escapes EnsureStartedAsync.
    /// </summary>
    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(HubException))]
    [InlineData(typeof(InvalidDataException))]
    public async Task A_start_that_fails_any_way_is_disconnected_not_an_exception(Type failure)
    {
        await using var hub = Hub(User("developer"), options => options.WebSocketFactory = (_, _) =>
            ValueTask.FromException<WebSocket>((Exception)Activator.CreateInstance(failure, "The start failed.")!));

        await hub.EnsureStartedAsync(CancellationToken.None);

        _dispatcher.Actions.Should().Equal(new HubStatusChanged(HubStatus.Disconnected));
    }

    /// <summary>
    ///     A cancellation the caller asked for is the caller's, so it is thrown, not reported as a status.
    ///     Red: catch every OperationCanceledException: nothing is thrown.
    /// </summary>
    [Fact]
    public async Task A_start_the_caller_cancels_is_thrown()
    {
        using var cancel = new CancellationTokenSource();
        await using var hub = Hub(User("developer"), options => options.WebSocketFactory = async (_, ct) =>
        {
            await cancel.CancelAsync();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The cancellation did not reach the start.");
        });

        await FluentActions.Awaiting(() => hub.EnsureStartedAsync(cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
        _dispatcher.Actions.Should().BeEmpty();
    }

    /// <summary>
    ///     After a failed start, a later call tries again at once rather than keep the failed connection.
    ///     Red: keep the failed connection: the second call returns without a try.
    /// </summary>
    [Fact]
    public async Task A_failed_start_is_tried_again_by_a_later_call()
    {
        var tries = 0;
        await using var hub = Hub(User("developer"), options => options.WebSocketFactory = (_, _) =>
        {
            Interlocked.Increment(ref tries);
            return ValueTask.FromException<WebSocket>(new TimeoutException("The start failed."));
        });

        await hub.EnsureStartedAsync(CancellationToken.None);
        await hub.EnsureStartedAsync(CancellationToken.None);

        tries.Should().Be(2);
        _dispatcher.Actions.Should().Equal(
            new HubStatusChanged(HubStatus.Disconnected), new HubStatusChanged(HubStatus.Disconnected));
    }

    /// <summary>
    ///     A failed start is tried again in the background, without a caller, and the tries stop once the connection is
    ///     disposed.
    ///     Red: start no background retry after a failed start: the tries stay at one.
    ///     Red: do not cancel the retries on dispose: the dispose never completes.
    /// </summary>
    [Fact]
    public async Task A_failed_start_is_tried_again_in_the_background_until_disposed()
    {
        var tries = 0;
        var hub = Hub(
            User("developer"),
            options => options.WebSocketFactory = (_, _) =>
            {
                Interlocked.Increment(ref tries);
                return ValueTask.FromException<WebSocket>(new TimeoutException("The start failed."));
            },
            new FixedDelay(TimeSpan.FromMilliseconds(10)));

        await hub.EnsureStartedAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref tries) < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }


        // DisposeAsync awaits the retries, so it completes only once they have stopped.
        await FluentActions.Awaiting(() => hub.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().NotThrowAsync("disposing stops the retries");
        Volatile.Read(ref tries).Should().BeGreaterThanOrEqualTo(3, "the failed start is tried again without a caller");
    }

    /// <summary>
    ///     The retry schedule: at once, then 2, 5 and 10 seconds, then 30 seconds for every try after, never giving up.
    ///     Red: return null after the fourth try: the later delays are null. Red: keep doubling past 10 seconds: the
    ///     later delays exceed the cap. Red: give up after a thousand tries: the 10,000th delay is null.
    /// </summary>
    [Fact]
    public void The_retry_schedule_is_capped_and_never_gives_up()
    {
        var policy = new HubRetryPolicy();

        TimeSpan? After(long retries) => policy.NextRetryDelay(new RetryContext { PreviousRetryCount = retries });

        Enumerable.Range(0, 7).Select(n => After(n)).Should().Equal(
            TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        After(10_000).Should().Be(HubRetryPolicy.Cap);
        After(long.MaxValue).Should().Be(HubRetryPolicy.Cap);
    }

    /// <summary>
    ///     A connection whose background retries wait an hour unless a test passes another policy, so a test sees only
    ///     the tries it makes itself.
    /// </summary>
    private RunHubConnection Hub(ClaimsPrincipal user, Action<HttpConnectionOptions>? transport = null, IRetryPolicy? retries = null)
    {
        var dispatcher = _provider.GetRequiredService<IDispatcher>();
        var api = _handler.Client();
        return new RunHubConnection(
            new ApiBaseAddress(new Uri($"http://127.0.0.1:{RefusedPort()}")),
            new FixedAuthentication(user),
            _provider,
            _dispatcher,
            new RunsEffects(api, dispatcher, _provider.GetRequiredService<IStore<RunsState>>()),
            new TasksEffects(api, dispatcher, _provider.GetRequiredService<IStore<TasksState>>()))
        {
            ConfigureTransport = transport,
            RetryPolicy = retries ?? new FixedDelay(TimeSpan.FromHours(1)),
        };
    }

    private sealed class FixedDelay(TimeSpan delay) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => delay;
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
