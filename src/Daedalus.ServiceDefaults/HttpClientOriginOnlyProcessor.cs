using System.Diagnostics;
using OpenTelemetry;

namespace Daedalus.ServiceDefaults;

/// <summary>
///     Cuts every outgoing HttpClient span down to the origin of its request: <c>url.full</c> keeps the scheme, host and
///     a non-default port, and loses the path, the query and any userinfo.
///     <para>
///         Why: on .NET 10 the HttpClient span is the runtime's own <c>System.Net.Http</c> span, and the runtime
///         redacts only the query. A path can carry a secret, such as the Telegram bot token in
///         <c>/bot{token}/{method}</c>, or other identifying names such as repositories and files. The span is
///         rewritten here, when it ends and before any exporter sees it, so it does not depend on which callbacks the
///         instrumentation runs for the runtime's spans. This covers failed calls, which end the same way, and every
///         redirect hop, which is a span of its own.
///     </para>
/// </summary>
internal sealed class HttpClientOriginOnlyProcessor : BaseProcessor<Activity>
{
    /// <summary>The runtime's HttpClient source, the only one that emits HttpClient spans on .NET 7 and later.</summary>
    private const string HttpSource = "System.Net.Http";

    private const string UrlFullTag = "url.full";

    /// <summary>Tags that could carry a path or a query, removed outright.</summary>
    private static readonly string[] PathTags = ["url.path", "url.query", "http.url", "http.target"];

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!string.Equals(data.Source.Name, HttpSource, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var tag in PathTags)
        {
            data.SetTag(tag, null);
        }

        if (data.GetTagItem(UrlFullTag) is string url)
        {
            data.SetTag(UrlFullTag, OriginOf(url));
        }
    }

    /// <summary>The origin of <paramref name="url"/>, or null (the tag is removed) when it is not an absolute URL.</summary>
    internal static string? OriginOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }
}
