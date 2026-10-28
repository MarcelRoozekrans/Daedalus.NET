namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     Reads the Ollama endpoint out of <c>ConnectionStrings:ollama</c>. Accepts <c>Endpoint=url</c> (the AppHost's shape),
///     <c>Endpoint=url;Model=name</c> (what the Aspire Ollama toolkit emitted) and a bare URL (local configuration).
/// </summary>
public static class OllamaConnectionString
{
    private const string EndpointKey = "Endpoint=";

    public static Uri ParseEndpoint(string connectionString)
    {
        var candidate = connectionString?.Trim() ?? string.Empty;
        if (candidate.StartsWith(EndpointKey, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[EndpointKey.Length..].Split(';', 2)[0].Trim();
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint)
            && (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                || string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            ? endpoint
            : throw new FormatException(
                "ConnectionStrings:ollama must be 'Endpoint=<url>', 'Endpoint=<url>;Model=<model>' or a bare http or https URL.");
    }
}
