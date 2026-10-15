using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Daedalus.Web;

/// <summary>
///     An <see cref="AuthenticationStateProvider" /> that always returns an authenticated user.
///     Used only when <c>TestMode=true</c> is set in configuration (E2E browser testing).
///     The authenticationType parameter ensures <see cref="ClaimsIdentity.IsAuthenticated" />
///     returns <c>true</c>, which makes <c>AuthorizeRouteView</c> render pages instead of
///     redirecting to the OIDC login flow.
/// </summary>
/// <remarks>
///     The user's roles are <see cref="DefaultRoles" /> unless the browser's <c>localStorage</c> holds a comma-separated
///     list under <see cref="RolesKey" />, which lets a browser test sign in as a narrower user in a context of its own.
///     A write to the key from another page of the same origin raises the <c>storage</c> event, which
///     <c>js/test-roles.js</c> passes to <see cref="OnRolesChanged" />: the roles are read again and the new state is
///     announced, as a sign-in that completes after the app rendered announces it. The identity claims never change.
///     This provider is registered only in test mode, so nothing else reads the key or loads the module.
/// </remarks>
public sealed class AlwaysAuthenticatedStateProvider(IJSRuntime js) : AuthenticationStateProvider, IAsyncDisposable
{
    /// <summary>The <c>localStorage</c> key a test sets to choose the user's roles.</summary>
    public const string RolesKey = "daedalus.test-roles";

    /// <summary>The roles of the test user when no test chose others.</summary>
    internal static readonly string[] DefaultRoles = ["admin", "task-manager", "project-manager", "analyst"];

    private Task<AuthenticationState>? _state;
    private DotNetObjectReference<AlwaysAuthenticatedStateProvider>? _self;
    private IJSObjectReference? _module;
    private IJSObjectReference? _watcher;

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state ??= CreateAsync(watch: true);

    /// <summary>Called from <c>js/test-roles.js</c> when another page of this origin wrote <see cref="RolesKey"/>.</summary>
    /// <remarks>
    ///     The state is announced already read, as the OIDC service announces a completed sign-in: an unfinished task
    ///     would make <c>AuthorizeRouteView</c> show its Authorizing content and build the page again, which hides
    ///     whether the page itself follows the change.
    /// </remarks>
    [JSInvokable]
    public async Task OnRolesChanged()
    {
        var state = await CreateAsync(watch: false);
        _state = Task.FromResult(state);
        NotifyAuthenticationStateChanged(_state);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_watcher is not null)
            {
                await _watcher.InvokeVoidAsync("dispose");
                await _watcher.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The page is gone, and its listener with it.
        }

        _self?.Dispose();
    }

    private async Task<AuthenticationState> CreateAsync(bool watch)
    {
        if (watch)
        {
            _self = DotNetObjectReference.Create(this);
            _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/test-roles.js");
            _watcher = await _module.InvokeAsync<IJSObjectReference>("watchRoles", _self, RolesKey);
        }

        var stored = await js.InvokeAsync<string?>("localStorage.getItem", RolesKey);
        var roles = string.IsNullOrWhiteSpace(stored)
            ? DefaultRoles
            : stored.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return new AuthenticationState(User(roles));
    }

    /// <summary>The signed-in test user with <paramref name="roles"/>.</summary>
    internal static ClaimsPrincipal User(IEnumerable<string> roles)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, "e2e-test-user"),
            new(ClaimTypes.Name, "E2E Test User"),
            new(ClaimTypes.Email, "e2e@daedalus.test"),
            .. roles.Select(role => new Claim(ClaimTypes.Role, role)),
        ];

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
