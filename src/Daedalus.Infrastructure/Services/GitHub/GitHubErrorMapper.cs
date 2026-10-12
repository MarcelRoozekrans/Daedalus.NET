using System.Globalization;
using System.Text;
using System.Text.Json;
using ZeroAlloc.Rest;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>
///     Turns every ZeroAlloc.Rest failure of the generated GitHub client, <c>IGitHubRestApi</c>, into a
///     <see cref="GitHubError"/>. It is total: every <see cref="HttpErrorKind"/> maps. The kind, the status and the retry
///     time build the message; the error body, read up to 2 KiB, is used only for <see cref="GitHubError.GitHubSaid"/>
///     and to recognise a secondary rate limit (ruling P9). <see cref="HttpError.Message"/>, an exception's text, is
///     never read.
/// </summary>
/// <param name="clock">The clock a <c>Retry-After</c> delay, or a secondary limit's minute, is added to.</param>
internal sealed class GitHubErrorMapper(TimeProvider clock) : IHttpErrorMapper<GitHubError>
{
    /// <summary>The longest <see cref="GitHubError.GitHubSaid"/>.</summary>
    internal const int MaxSaidLength = 300;

    /// <summary>The largest Unix time <see cref="DateTimeOffset.FromUnixTimeSeconds"/> accepts, 9999-12-31T23:59:59Z.</summary>
    private const long MaxUnixSeconds = 253_402_300_799;

    /// <summary>GitHub's documented minimum wait after a rate limit that names no time.</summary>
    private static readonly TimeSpan RateLimitMinimumWait = TimeSpan.FromSeconds(60);

    public GitHubError Map(HttpError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.Kind switch
        {
            HttpErrorKind.Status => FromStatus(error),
            HttpErrorKind.Timeout => GitHubError.TimedOut(),
            HttpErrorKind.Transport => GitHubError.NotSent(),
            _ => GitHubError.Unreadable("GitHub's answer could not be read.", (int)error.StatusCode == 0 ? null : (int)error.StatusCode),
        };
    }

    /// <summary>
    ///     GitHub's own words: the top-level <c>message</c>, then each <c>errors[]</c> entry as <c>code field: message</c>, or
    ///     the entry itself when it is a string, joined with "; ", on one line of at most <see cref="MaxSaidLength"/>
    ///     characters. <see langword="null"/> for an empty body or one that is not JSON.
    /// </summary>
    internal static string? Summarize(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var parts = new List<string>();
            if (Text(document.RootElement, "message") is { } message)
                parts.Add(message);

            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in errors.EnumerateArray())
                {
                    if (Entry(entry) is { } text)
                        parts.Add(text);
                }
            }

            return parts.Count == 0 ? null : OneLine(string.Join("; ", parts));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // A string with invalid UTF-8 parses, then GetString throws this.
            return null;
        }
    }

    private GitHubError FromStatus(HttpError error)
    {
        var status = (int)error.StatusCode;
        DateTimeOffset? retryAfter = error.GetRetryAfter(clock) is { } wait ? clock.GetUtcNow() + wait : null;
        var exhausted = string.Equals(First(error.Headers, "X-RateLimit-Remaining")?.Trim(), "0", StringComparison.Ordinal);

        // A secondary limit may carry neither header; GitHub then names it only in the body, which is read for this test
        // even when it was cut at 2 KiB.
        var secondary = status is 403 or 429
            && Encoding.UTF8.GetString(error.Body.Span).Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase);

        var kind = status switch
        {
            429 => GitHubErrorKind.RateLimited,
            403 when exhausted || retryAfter is not null || secondary => GitHubErrorKind.RateLimited,
            401 => GitHubErrorKind.Unauthorized,
            403 => GitHubErrorKind.Forbidden,
            404 or 410 => GitHubErrorKind.NotFound,
            408 => GitHubErrorKind.Timeout,
            409 => GitHubErrorKind.Conflict,
            >= 500 and <= 599 => GitHubErrorKind.Server,
            >= 400 and <= 499 => GitHubErrorKind.Validation,
            // Only a 1xx, 2xx or 3xx reaches here, which a ZeroAlloc.Rest status failure does not produce today; it is
            // read as a server fault rather than left unmapped.
            _ => GitHubErrorKind.Server,
        };

        // GitHub's rule: honour Retry-After when it is present; otherwise wait for X-RateLimit-Reset, but only when the
        // remaining count is 0, because GitHub sends the reset on every response; otherwise wait at least one minute.
        var retryAt = retryAfter ?? (exhausted ? Reset(error.Headers) : null) ?? clock.GetUtcNow() + RateLimitMinimumWait;
        return GitHubError.FromStatus(status, kind, retryAt) with
        {
            GitHubSaid = error.BodyTruncated ? null : Summarize(error.Body),
        };
    }

    private static string? Entry(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String)
            return entry.GetString() is { Length: > 0 } text ? text : null;
        if (entry.ValueKind != JsonValueKind.Object)
            return null;

        var head = string.Join(' ', new[] { Text(entry, "code"), Text(entry, "field") }.OfType<string>());
        var message = Text(entry, "message");
        return (head.Length, message) switch
        {
            (0, null) => null,
            (0, _) => message,
            (_, null) => head,
            _ => $"{head}: {message}",
        };
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>Control characters and runs of white space become one space; longer than the cap ends in "…".</summary>
    private static string OneLine(string text)
    {
        var line = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var blank = char.IsControl(c) || char.IsWhiteSpace(c);
            if (!blank)
                line.Append(c);
            else if (line.Length > 0 && line[^1] != ' ')
                line.Append(' ');
        }

        var trimmed = line.ToString().TrimEnd();
        return trimmed.Length <= MaxSaidLength ? trimmed : string.Concat(trimmed.AsSpan(0, MaxSaidLength - 1), "…");
    }

    /// <summary>The reset time, never earlier than now: a reset already past would read "rate-limited until" a past time.</summary>
    private DateTimeOffset? Reset(IReadOnlyDictionary<string, IReadOnlyList<string>> headers)
    {
        if (!long.TryParse(First(headers, "X-RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > MaxUnixSeconds)
            return null;

        var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var now = clock.GetUtcNow();
        return reset < now ? now : reset;
    }

    /// <summary>The first value of <paramref name="name"/>. The generated client's dictionary ignores case; a hand-built one may not.</summary>
    private static string? First(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name)
    {
        if (headers.TryGetValue(name, out var values))
            return values.Count > 0 ? values[0] : null;

        var other = headers.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        return other is { Count: > 0 } ? other[0] : null;
    }
}
