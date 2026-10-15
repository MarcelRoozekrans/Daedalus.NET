using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.JSInterop;

namespace Daedalus.Web.Auth;

/// <summary>
///     Ends a session whose token can no longer be refreshed. The OIDC user stored in the browser is cleared, the console
///     treats the user as signed out, and the browser goes to sign-in, which returns to the page the user was on.
/// </summary>
/// <remarks>
///     A failed silent refresh leaves the OIDC client's stored user in place, and the authentication state goes on
///     reading it, so without the clear the header would still name the user of a dead session. <see cref="Expired"/>
///     covers the moment before the browser leaves for the identity provider; the layout clears it again when the
///     authentication state changes, as it does after a sign-in that completes in place. One instance per app, shared
///     by the API client's handler and the run hub, so a second failure while the first redirect is under way does not
///     redirect again.
/// </remarks>
public sealed class SessionExpiry(IJSRuntime js, NavigationManager navigation)
{
    /// <summary>The failure a request returns once its session has ended; the browser is already on its way to sign in.</summary>
    public const string SigningInAgain = "Your session has ended. Signing in again.";

    /// <summary>Whether the session has ended and sign-in is under way.</summary>
    public bool Expired { get; private set; }

    /// <summary>Raised when <see cref="Expired"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The exception a request for <paramref name="result"/> would have thrown, to hand to <see cref="SignInAgainAsync"/>.</summary>
    public AccessTokenNotAvailableException Unavailable(AccessTokenResult result) => new(navigation, result, scopes: null);

    /// <summary>
    ///     Clears the stored OIDC user and sends the browser to sign in, as <paramref name="exception"/> says, returning to
    ///     the current page. Once only until <see cref="Reset"/>.
    /// </summary>
    public async Task SignInAgainAsync(AccessTokenNotAvailableException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Expired)
        {
            return;
        }

        Expired = true;
        Changed?.Invoke(this, EventArgs.Empty);
        await ClearStoredUserAsync();
        exception.Redirect();
    }

    /// <summary>A new authentication state arrived: whatever it says now decides, not the session that ended.</summary>
    public void Reset()
    {
        if (!Expired)
        {
            return;
        }

        Expired = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ClearStoredUserAsync()
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/session.js");
            await module.InvokeVoidAsync("clearStoredUser");
        }
        catch (JSException)
        {
            // The redirect still goes ahead: sign-in replaces the stored user, and a failed clear must not keep the
            // user on a page whose session has ended.
        }
    }
}
