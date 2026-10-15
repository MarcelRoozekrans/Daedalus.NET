using Daedalus.Web.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.JSInterop;

namespace Daedalus.Tests.Unit.Web;

/// <summary>A navigation manager at <see cref="Page"/> that records every navigation instead of making it.</summary>
internal sealed class RecordingNavigation : NavigationManager
{
    public const string Page = "http://localhost/tasks";

    public RecordingNavigation() => Initialize("http://localhost/", Page);

    /// <summary>Each navigation: where it went and the history state it carried.</summary>
    public List<(string Uri, string? State)> Navigations { get; } = [];

    protected override void NavigateToCore(string uri, NavigationOptions options) =>
        Navigations.Add((uri, options.HistoryEntryState));

    /// <summary>The token failure the OIDC provider reports for a silent refresh against a dead session.</summary>
    public AccessTokenResult RefreshFailed() => new(
        AccessTokenResultStatus.RequiresRedirect,
        token: null!,
        "authentication/login",
        new InteractiveRequestOptions { Interaction = InteractionType.GetToken, ReturnUrl = Uri });
}

/// <summary>A JS runtime that records each call, by its identifier, on itself and on every module it imports.</summary>
internal sealed class RecordingJsRuntime : IJSRuntime
{
    public List<string> Calls { get; } = [];

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Calls.Add(args is [string first, ..] ? $"{identifier} {first}" : identifier);
        return ValueTask.FromResult(typeof(TValue) == typeof(IJSObjectReference) ? (TValue)(object)new Module(this) : default!);
    }

    private sealed class Module(RecordingJsRuntime runtime) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            runtime.Calls.Add($"module.{identifier}");
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A token handler that cannot get a token: every request throws, as AuthorizationMessageHandler does.</summary>
internal sealed class TokenRefreshFails(RecordingNavigation navigation) : HttpMessageHandler
{
    public int Sent { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Sent++;
        throw new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null);
    }
}

/// <summary>An API client's HTTP client, as Program.cs builds it, over a token handler that cannot get a token.</summary>
internal sealed class ExpiredSession : IDisposable
{
    public ExpiredSession()
    {
        Expiry = new SessionExpiry(Js, Navigation);
        Http = new HttpClient(new SessionExpiryHandler(Expiry) { InnerHandler = new TokenRefreshFails(Navigation) })
        {
            BaseAddress = new Uri("http://localhost"),
        };
    }

    public RecordingNavigation Navigation { get; } = new();

    public RecordingJsRuntime Js { get; } = new();

    public SessionExpiry Expiry { get; }

    public HttpClient Http { get; }

    public void Dispose() => Http.Dispose();
}
