namespace Daedalus.Web.Services;

/// <summary>A response's status as text, for a failure message: its code and its reason.</summary>
public static class HttpStatusText
{
    /// <summary>
    ///     The status code and the reason phrase, such as "400 Bad Request". In the browser the reason is the fetch
    ///     response's <c>statusText</c>, which is empty over HTTP/2, so an empty reason falls back to the status name.
    /// </summary>
    public static string Of(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? response.StatusCode.ToString() : response.ReasonPhrase;
        return $"{(int)response.StatusCode} {reason}";
    }
}
