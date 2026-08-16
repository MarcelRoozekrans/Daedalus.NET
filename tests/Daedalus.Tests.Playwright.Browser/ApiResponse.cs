namespace Daedalus.Tests.Playwright.Browser;

/// <summary>
///     Lightweight wrapper around HttpResponseMessage that provides an API similar to
///     Playwright's IAPIResponse for browser tests that also make direct API calls.
/// </summary>
public sealed class ApiResponse(HttpResponseMessage response)
{
    /// <summary>HTTP status code.</summary>
    public int Status => (int)response.StatusCode;

    /// <summary>True if status is 2xx.</summary>
    public bool Ok => response.IsSuccessStatusCode;

    /// <summary>Status text (reason phrase).</summary>
    public string StatusText => response.ReasonPhrase ?? string.Empty;

    /// <summary>Response headers (combined response + content headers).</summary>
    public IReadOnlyDictionary<string, string> Headers
    {
        get
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers)
            {
                dict[header.Key] = string.Join(", ", header.Value);
            }

            foreach (var header in response.Content.Headers)
            {
                dict[header.Key] = string.Join(", ", header.Value);
            }

            return dict;
        }
    }

    /// <summary>Read the response body as a string.</summary>
    public Task<string> TextAsync() => response.Content.ReadAsStringAsync();

    /// <summary>Read the response body as bytes.</summary>
    public Task<byte[]> BodyAsync() => response.Content.ReadAsByteArrayAsync();
}
