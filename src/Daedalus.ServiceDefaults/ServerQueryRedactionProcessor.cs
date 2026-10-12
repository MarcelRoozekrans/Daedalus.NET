using System.Diagnostics;
using OpenTelemetry;

namespace Daedalus.ServiceDefaults;

/// <summary>
///     Redacts the value of any <c>access_token</c> query parameter on ASP.NET Core server spans before export. The
///     console's hub connection carries its bearer token there, because a browser cannot set headers on a WebSocket.
///     The instrumentation redacts every query value by default, but one setting,
///     <c>OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION</c>, turns that off; this processor keeps the
///     token out of <c>url.query</c> whatever that setting says.
/// </summary>
internal sealed class ServerQueryRedactionProcessor : BaseProcessor<Activity>
{
    /// <summary>The ASP.NET Core server source, which emits the incoming request spans.</summary>
    internal const string AspNetCoreSource = "Microsoft.AspNetCore";

    private const string QueryTag = "url.query";
    private const string TokenName = "access_token";

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.Equals(data.Source.Name, AspNetCoreSource, StringComparison.Ordinal)
            && data.GetTagItem(QueryTag) is string query
            && Redact(query) is { } redacted)
        {
            data.SetTag(QueryTag, redacted);
        }
    }

    /// <summary>
    ///     The query with every <c>access_token</c> value replaced by <c>Redacted</c>, the name compared
    ///     case-insensitively, or <see langword="null"/> when it has none.
    /// </summary>
    internal static string? Redact(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var prefix = query.StartsWith('?') ? "?" : string.Empty;
        var pairs = query[prefix.Length..].Split('&');
        var changed = false;
        for (var i = 0; i < pairs.Length; i++)
        {
            var name = pairs[i].Split('=', 2)[0];
            if (string.Equals(name, TokenName, StringComparison.OrdinalIgnoreCase))
            {
                pairs[i] = name + "=Redacted";
                changed = true;
            }
        }

        return changed ? prefix + string.Join('&', pairs) : null;
    }
}
