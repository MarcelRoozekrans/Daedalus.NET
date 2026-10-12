using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Daedalus.Api.Hubs;

/// <summary>Reads a bearer token from <c>access_token</c> for a hub request only, since a browser WebSocket cannot send headers.</summary>
public static class HubAccessToken
{
    /// <summary>The path segment every hub is mapped under.</summary>
    private const string HubsPath = "/hubs";

    /// <summary>The query parameter a SignalR client puts its bearer token in.</summary>
    private const string QueryParameter = "access_token";

    /// <summary>The <see cref="JwtBearerEvents.OnMessageReceived"/> handler.</summary>
    public static Task ReadFromQueryAsync(MessageReceivedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HttpContext.Request.Path.StartsWithSegments(HubsPath, StringComparison.OrdinalIgnoreCase)
            && context.Request.Query[QueryParameter] is { Count: > 0 } token
            && !string.IsNullOrEmpty(token[0]))
        {
            context.Token = token[0];
        }

        return Task.CompletedTask;
    }
}
