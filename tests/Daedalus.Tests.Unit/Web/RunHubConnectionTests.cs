using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Claims;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.Auth;
using Daedalus.Web.State;
using Daedalus.Web.State.Projects;
using Daedalus.Web.State.Runs;
using Daedalus.Web.State.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>The run hub connection: its address, who it opens for, and that a failed start is a status, not an exception.</summary>
public sealed class RunHubConnectionTests : IDisposable
{
    /// <summary>The awaiting-approval count's request, which every refresh makes.</summary>
    private const string CountPath = "/api/workflow-runs?take=100&status=Awaiting";

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly RecordingLogger _logger = new();

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
    ///     A failed start is logged once at Warning with the exception's type name and nothing else: the hub URL
    ///     carries the access token in its query, and the exception's message can repeat it.
    ///     Red: drop the log: no entry is recorded.
    ///     Red: log the exception object or its message: the entry carries an exception, or the token-bearing text.
    /// </summary>
    [Fact]
    public async Task A_failed_start_logs_only_the_exception_type_name()
    {
        await using var hub = Hub(User("developer"), options => options.WebSocketFactory = (_, _) =>
            ValueTask.FromException<WebSocket>(new TimeoutException("wss://h/hubs/workflow-runs?access_token=SECRET")));

        await hub.EnsureStartedAsync(CancellationToken.None);

        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Be("The run hub failed to start: TimeoutException");
        entry.Message.Should().NotContain("SECRET");
        entry.Exception.Should().BeNull("the exception object would carry the token-bearing message");
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
    ///     A hub token that cannot be refreshed ends the session as an API request's does: the stored user is cleared
    ///     and the browser goes to sign-in, and the start is Disconnected, not a connection without a token.
    ///     Red: restore the old ReadTokenAsync, which returned null when no token came; nothing is navigated.
    ///     Red: skip ClearStoredUserAsync in SessionExpiry.SignInAgainAsync; no clearStoredUser call is recorded.
    ///     Red: exclude AccessTokenNotAvailableException from TryStartAsync's catch; the exception escapes
    ///     EnsureStartedAsync instead of a Disconnected status.
    /// </summary>
    [Fact]
    public async Task A_hub_token_that_cannot_be_refreshed_signs_in_again()
    {
        var navigation = new RecordingNavigation();
        var js = new RecordingJsRuntime();
        var tokens = Substitute.For<IAccessTokenProvider>();
        tokens.RequestAccessToken().Returns(ValueTask.FromResult(navigation.RefreshFailed()));
        await using var services = new ServiceCollection()
            .AddSingleton(tokens)
            .AddSingleton(new SessionExpiry(js, navigation, new ImmediateTime()))
            .BuildServiceProvider();
        await using var hub = Hub(User("developer"), services: services);

        await hub.EnsureStartedAsync(CancellationToken.None);

        navigation.Navigations.Should().ContainSingle().Which.Uri.Should().Be("authentication/login");
        js.Calls.Should().Contain("module.clearStoredUser");
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
    ///     A start that succeeds after a failed one re-fetches what is on screen, because changes sent while no
    ///     connection was open are lost; here the awaiting-approval count, which a refresh always fetches.
    ///     Red: drop the re-fetch after a start that recovers: no request is made.
    /// </summary>
    [Fact]
    public async Task A_start_that_recovers_from_a_failure_re_fetches_what_is_on_screen()
    {
        using var hubServer = new HandshakingHub();
        var tries = 0;
        var hub = Hub(
            User("developer"),
            options => options.WebSocketFactory = (_, ct) => Interlocked.Increment(ref tries) == 1
                ? ValueTask.FromException<WebSocket>(new TimeoutException("The first start failed."))
                : hubServer.AcceptAsync(ct),
            new FixedDelay(TimeSpan.FromMilliseconds(10)));
        _handler.OnJson(CountPath, RunViews.Json(new WorkflowRunList([], null)));

        try
        {
            await hub.EnsureStartedAsync(CancellationToken.None);
            _handler.Paths.Should().BeEmpty("the first start failed, so nothing was fetched");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!_handler.Paths.Contains(CountPath, StringComparer.Ordinal) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }
        finally
        {
            await hub.DisposeAsync();
        }

        _dispatcher.Actions.Take(2).Should().Equal(
            new HubStatusChanged(HubStatus.Disconnected), new HubStatusChanged(HubStatus.Connected));
        _handler.Paths.Should().Contain(CountPath, "the recovered start re-fetches what is on screen");
    }

    /// <summary>
    ///     A start that recovers from a failure re-fetches the projects once a reader has loaded them, because a project
    ///     may have been added or renamed while no connection was open.
    ///     Red: drop the projects refresh from RunHubConnection.RefreshAsync; the projects are not requested again.
    /// </summary>
    [Fact]
    public async Task A_start_that_recovers_re_fetches_the_projects_once_they_were_asked_for()
    {
        const string projectsPath = "/api/projects?page=1&pageSize=200";
        using var hubServer = new HandshakingHub();
        var tries = 0;
        var hub = Hub(
            User("developer"),
            options => options.WebSocketFactory = (_, ct) => Interlocked.Increment(ref tries) == 1
                ? ValueTask.FromException<WebSocket>(new TimeoutException("The first start failed."))
                : hubServer.AcceptAsync(ct),
            new FixedDelay(TimeSpan.FromMilliseconds(10)));
        _handler.OnJson(CountPath, RunViews.Json(new WorkflowRunList([], null)))
            .OnJson(projectsPath, RunViews.Json(new PagedResultDto<ProjectDto>([], 0, 1, 200)));
        await new ProjectsEffects(_handler.Client(), _handler.ProjectClient(), _provider.GetRequiredService<IDispatcher>(), _provider.GetRequiredService<IStore<ProjectsState>>(), new RecordingLogger<ProjectsEffects>())
            .LoadAsync(CancellationToken.None);
        _handler.Requests.Clear();

        try
        {
            await hub.EnsureStartedAsync(CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!_handler.Paths.Contains(projectsPath, StringComparer.Ordinal) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }
        finally
        {
            await hub.DisposeAsync();
        }

        _handler.Paths.Should().Contain(projectsPath, "the recovered start re-fetches the projects a reader loaded");
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
    private RunHubConnection Hub(
        ClaimsPrincipal user,
        Action<HttpConnectionOptions>? transport = null,
        IRetryPolicy? retries = null,
        IServiceProvider? services = null)
    {
        var dispatcher = _provider.GetRequiredService<IDispatcher>();
        var api = _handler.Client();
        return new RunHubConnection(
            new ApiBaseAddress(new Uri($"http://127.0.0.1:{RefusedPort()}")),
            new FixedAuthentication(user),
            services ?? _provider,
            _dispatcher,
            new RunsEffects(api, dispatcher, _provider.GetRequiredService<IStore<RunsState>>()),
            new TasksEffects(api, dispatcher, _provider.GetRequiredService<IStore<TasksState>>()),
            new ProjectsEffects(api, _handler.ProjectClient(), dispatcher, _provider.GetRequiredService<IStore<ProjectsState>>(), new RecordingLogger<ProjectsEffects>()),
            _logger)
        {
            ConfigureTransport = transport,
            RetryPolicy = retries ?? new FixedDelay(TimeSpan.FromHours(1)),
        };
    }

    private sealed class FixedDelay(TimeSpan delay) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => delay;
    }

    /// <summary>
    ///     The server end of a hub connection over a loopback socket pair: it answers SignalR's JSON handshake with an
    ///     empty handshake response, then reads until the client closes.
    /// </summary>
    private sealed class HandshakingHub : IDisposable
    {
        private const byte RecordSeparator = 0x1e;

        /// <summary>An empty JSON handshake response, <c>{}</c>, and the record separator that ends it.</summary>
        private static readonly byte[] HandshakeResponse = [(byte)'{', (byte)'}', RecordSeparator];

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<IDisposable> _owned = [];

        public HandshakingHub() => _listener.Start();

        /// <summary>Connects a client socket to a fresh server end and returns the client's WebSocket.</summary>
        public async ValueTask<WebSocket> AcceptAsync(CancellationToken ct)
        {
            var client = new TcpClient();
            _owned.Add(client);
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port, ct);
            var server = await _listener.AcceptTcpClientAsync(ct);
            _owned.Add(server);
            var serverSocket = WebSocket.CreateFromStream(server.GetStream(), isServer: true, subProtocol: null, Timeout.InfiniteTimeSpan);
            _owned.Add(serverSocket);
            _ = ServeAsync(serverSocket);
            return WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            foreach (var owned in _owned)
            {
                owned.Dispose();
            }

            _listener.Stop();
            _listener.Dispose();
        }

        private static async Task ServeAsync(WebSocket server)
        {
            var buffer = new byte[4096];
            try
            {
                // The client's handshake request ends with the record separator.
                WebSocketReceiveResult received;
                do
                {
                    received = await server.ReceiveAsync(buffer, CancellationToken.None);
                }
                while (received.Count == 0 || buffer[received.Count - 1] != RecordSeparator);

                await server.SendAsync(HandshakeResponse, WebSocketMessageType.Text, true, CancellationToken.None);
                while (received.MessageType != WebSocketMessageType.Close)
                {
                    received = await server.ReceiveAsync(buffer, CancellationToken.None);
                }

                await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or IOException)
            {
                // The test tore the socket pair down first.
            }
        }
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

    private sealed class RecordingLogger : ILogger<RunHubConnection>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
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
