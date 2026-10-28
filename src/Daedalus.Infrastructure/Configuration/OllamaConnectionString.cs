namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     Reads the Ollama endpoint out of <c>ConnectionStrings:ollama</c>. Accepts <c>Endpoint=url</c> (the AppHost's shape),
///     <c>Endpoint=url;Model=name</c> (what the Aspire Ollama toolkit emitted) and a bare URL (local configuration).
///     The <c>Endpoint</c> segment may sit at any position, so <c>Model=m;Endpoint=url</c> reads too.
/// </summary>
public static class OllamaConnectionString
{
    private const string EndpointKey = "Endpoint=";

    /// <param name="connectionString">The configured value; null or blank is refused like any other unreadable value.</param>
    /// <exception cref="FormatException">The value holds no http or https endpoint.</exception>
    public static Uri ParseEndpoint(string? connectionString)
    {
        var candidate = connectionString?.Trim() ?? string.Empty;
        var keyed = candidate.Split(';', StringSplitOptions.TrimEntries)
            .FirstOrDefault(segment => segment.StartsWith(EndpointKey, StringComparison.OrdinalIgnoreCase));
        if (keyed is not null)
        {
            candidate = keyed[EndpointKey.Length..].Trim();
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint)
            && (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                || string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            ? endpoint
            : throw new FormatException(
                "ConnectionStrings:ollama must be 'Endpoint=<url>', 'Endpoint=<url>;Model=<model>' or a bare http or https URL.");
    }
}
