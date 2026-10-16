using System.Diagnostics;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.Auth;
using Daedalus.Web.State.Home;
using Daedalus.Web.State.Projects;
using Daedalus.Web.State.Runs;
using Daedalus.Web.State.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State;

/// <summary>
///     The console's one live connection. Opened once per app for a user who may view runs; every
///     <c>RunChanged</c> becomes a re-fetch, and a reconnect re-fetches everything on screen, because messages sent
///     while disconnected are lost. A dropped connection and a start that failed are both tried again on
///     <see cref="HubRetryPolicy"/>'s schedule, without end, until the connection is disposed.
/// </summary>
public sealed partial class RunHubConnection(
    ApiBaseAddress api,
    AuthenticationStateProvider authentication,
    IServiceProvider services,
    IDispatcher dispatcher,
    RunsEffects runs,
    TasksEffects tasks,
    ProjectsEffects projects,
    HomeEffects home,
    ILogger<RunHubConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposing = new();
    private HubConnection? _connection;
    private Task _retrying = Task.CompletedTask;
    private bool _missedChanges;

    /// <summary>
    ///     When a start that failed is tried again, and when a dropped connection reconnects. A test seam, reached
    ///     through <c>InternalsVisibleTo</c> from <c>Daedalus.Tests.Unit</c>.
    /// </summary>
    internal IRetryPolicy RetryPolicy { get; init; } = new HubRetryPolicy();

    /// <summary>
    ///     Adjusts the transport options after the console's own, for a test that needs a start to fail a given way.
    ///     A test seam, reached through <c>InternalsVisibleTo</c> from <c>Daedalus.Tests.Unit</c>.
    /// </summary>
    internal Action<HttpConnectionOptions>? ConfigureTransport { get; init; }

    /// <summary>
    ///     Opens the connection once, for a user who may view runs. A user who may not is left unconnected, so a later
    ///     call after sign-in can still open it. A failed start is <see cref="HubStatus.Disconnected"/>, never an
    ///     exception, whatever it failed with, so the pages still load on navigation; only a cancellation of
    ///     <paramref name="ct"/> itself is thrown. A failed start is tried again in the background, and a later call
    ///     tries again at once.
    /// </summary>
    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is not null)
            {
                return;
            }

            var user = (await authentication.GetAuthenticationStateAsync()).User;
            if (!RunAccess.CanView(user))
            {
                return;
            }

            if (!await TryStartAsync(ct) && _retrying.IsCompleted)
            {
                _retrying = RetryStartAsync(_disposing.Token);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _disposing.CancelAsync();
        await _retrying;
        await _gate.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
        _disposing.Dispose();
    }

    /// <summary>
    ///     The hub's address for an API at <paramref name="apiBase"/>. The hub path is root-relative, as every
    ///     <see cref="ApiClient"/> route is, so the hub and the API resolve against the same origin whatever path or
    ///     trailing slash the base has.
    /// </summary>
    public static Uri HubUri(Uri apiBase) => new(apiBase, WorkflowRunsHubContract.Path);

    /// <summary>
    ///     Builds a connection and starts it, under the gate. On success it becomes the connection; on any failure but
    ///     a cancellation of <paramref name="ct"/> it is disposed and the status is Disconnected.
    /// </summary>
    private async Task<bool> TryStartAsync(CancellationToken ct)
    {
        var connection = Build();
        connection.On<WorkflowRunChanged>(WorkflowRunsHubContract.RunChangedMethod, OnChangedAsync);
        connection.Reconnecting += _ => SetStatusAsync(HubStatus.Reconnecting);
        connection.Reconnected += _ => OnReconnectedAsync();
        connection.Closed += _ => SetStatusAsync(HubStatus.Disconnected);

        var started = false;
        try
        {
            await connection.StartAsync(ct);
            started = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Every failure, not a list of the expected ones: a handshake timeout, a hub error or an unreadable
            // handshake would otherwise escape into the layout and replace every page with Blazor's error UI.
            // Only the type name is logged: the hub URL carries the access token in its query, and an exception's
            // message or ToString can repeat it.
            LogStartFailed(logger, exception.GetType().Name);
        }
        finally
        {
            if (!started)
            {
                await connection.DisposeAsync();
            }
        }

        if (!started)
        {
            _missedChanges = true;
            await SetStatusAsync(HubStatus.Disconnected);
            return false;
        }

        _connection = connection;
        await SetStatusAsync(HubStatus.Connected);
        if (_missedChanges)
        {
            // Changes sent while no connection was open are lost, as across a reconnect.
            _missedChanges = false;
            await RefreshAsync();
        }

        return true;
    }

    /// <summary>
    ///     Tries a failed start again on <see cref="RetryPolicy"/>'s schedule, from its second delay on, since the
    ///     failed start was the first try, until a start succeeds, a caller's start already did, or the connection is
    ///     disposed.
    /// </summary>
    private async Task RetryStartAsync(CancellationToken stopping)
    {
        var elapsed = Stopwatch.StartNew();
        var retries = 1L;
        try
        {
            while (RetryPolicy.NextRetryDelay(new RetryContext { PreviousRetryCount = retries++, ElapsedTime = elapsed.Elapsed }) is { } wait)
            {
                await Task.Delay(wait, stopping);
                await _gate.WaitAsync(stopping);
                try
                {
                    if (_connection is not null || await TryStartAsync(stopping))
                    {
                        return;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Disposed: stop trying.
        }
    }

    private HubConnection Build() => new HubConnectionBuilder()
        .WithUrl(HubUri(api.Value), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.AccessTokenProvider = ReadTokenAsync;
            ConfigureTransport?.Invoke(options);
        })
        .WithAutomaticReconnect(RetryPolicy)
        .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, WorkflowRunsJsonContext.Default))
        .Build();

    /// <summary>
    ///     The bearer token for the hub, or none in test mode, where no token provider is registered. A token that cannot
    ///     be refreshed ends the session through <see cref="SessionExpiry"/>, as an API request's does, and fails the
    ///     start or reconnect with the provider's exception instead of connecting without a token.
    /// </summary>
    private async Task<string?> ReadTokenAsync()
    {
        if (services.GetService<IAccessTokenProvider>() is not { } provider)
        {
            return null;
        }

        var result = await provider.RequestAccessToken();
        if (result.TryGetToken(out var token))
        {
            return token.Value;
        }

        var expiry = services.GetRequiredService<SessionExpiry>();
        var unavailable = expiry.Unavailable(result);
        await expiry.SignInAgainAsync(unavailable);
        throw unavailable;
    }

    private async Task OnChangedAsync(WorkflowRunChanged change)
    {
        await runs.OnRunChangedAsync(change, CancellationToken.None);
        await tasks.OnRunChangedAsync(change, CancellationToken.None);
        await home.OnRunChangedAsync(change, CancellationToken.None);
    }

    [LoggerMessage(EventId = 3100, Level = LogLevel.Warning, Message = "The run hub failed to start: {ExceptionType}")]
    private static partial void LogStartFailed(ILogger logger, string exceptionType);

    private async Task SetStatusAsync(HubStatus status) => await dispatcher.DispatchAsync(new HubStatusChanged(status));

    private async Task OnReconnectedAsync()
    {
        await SetStatusAsync(HubStatus.Connected);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        await runs.RefreshAllAsync(CancellationToken.None);
        await tasks.RefreshAsync(CancellationToken.None);
        await projects.RefreshAsync(CancellationToken.None);
        await home.RefreshAsync(CancellationToken.None);
    }
}
