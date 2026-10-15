using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace Daedalus.Web.Auth;

/// <summary>
///     The outermost handler on the API client. When the token handler inside it cannot get a token, it ends the session
///     through <see cref="SessionExpiry"/> and lets the exception go on, so every caller, whichever client or page made
///     the request, fails the request and the user goes to sign in.
/// </summary>
public sealed class SessionExpiryHandler(SessionExpiry expiry) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (AccessTokenNotAvailableException exception)
        {
            await expiry.SignInAgainAsync(exception);
            throw;
        }
    }
}
