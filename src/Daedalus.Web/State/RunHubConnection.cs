using System.Net.WebSockets;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.Auth;
using Daedalus.Web.State.Runs;
using Daedalus.Web.State.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State;

/// <summary>
///     The console's one live connection. Opened once per app for a user who may view runs; every
///     <c>RunChanged</c> becomes a re-fetch, and a reconnect re-fetches everything on screen, because messages sent
///     while disconnected are lost.
/// </summary>
public sealed class RunHubConnection(
    ApiBaseAddress api,
    AuthenticationStateProvider authentication,
    IServiceProvider services,
    IDispatcher dispatcher,
    RunsEffects runs,
    TasksEffects tasks) : IAsyncDisposable
{
    private const string Path = "hubs/workflow-runs";
    private const string RunChangedMethod = "RunChanged";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _connection;

    /// <summary>
    ///     Opens the connection once, for a user who may view runs. A user who may not is left unconnected, so a later
    ///     call after sign-in can still open it. A failed start is <see cref="HubStatus.Disconnected"/>, never an
    ///     exception: the pages still load on navigation.
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

            _connection = Build();
            _connection.On<WorkflowRunChanged>(RunChangedMethod, OnChangedAsync);
            _connection.Reconnecting += _ => SetStatusAsync(HubStatus.Reconnecting);
            _connection.Reconnected += _ => OnReconnectedAsync();
            _connection.Closed += _ => SetStatusAsync(HubStatus.Disconnected);

            try
            {
                await _connection.StartAsync(ct);
                await dispatcher.DispatchAsync(new HubStatusChanged(HubStatus.Connected));
            }
            catch (Exception exception) when (exception is HttpRequestException or WebSocketException or InvalidOperationException)
            {
                await dispatcher.DispatchAsync(new HubStatusChanged(HubStatus.Disconnected));
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
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }

    private HubConnection Build() => new HubConnectionBuilder()
        .WithUrl(new Uri(api.Value, Path), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.AccessTokenProvider = ReadTokenAsync;
        })
        .WithAutomaticReconnect()
        .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, WorkflowRunsJsonContext.Default))
        .Build();

    /// <summary>The bearer token for the hub, or none in test mode, where no token provider is registered.</summary>
    private async Task<string?> ReadTokenAsync()
    {
        if (services.GetService<IAccessTokenProvider>() is not { } provider)
        {
            return null;
        }

        var result = await provider.RequestAccessToken();
        return result.TryGetToken(out var token) ? token.Value : null;
    }

    private async Task OnChangedAsync(WorkflowRunChanged change)
    {
        await runs.OnRunChangedAsync(change, CancellationToken.None);
        await tasks.OnRunChangedAsync(change, CancellationToken.None);
    }

    private async Task SetStatusAsync(HubStatus status) => await dispatcher.DispatchAsync(new HubStatusChanged(status));

    private async Task OnReconnectedAsync()
    {
        await SetStatusAsync(HubStatus.Connected);
        await runs.RefreshAllAsync(CancellationToken.None);
        await tasks.RefreshAsync(CancellationToken.None);
    }
}
