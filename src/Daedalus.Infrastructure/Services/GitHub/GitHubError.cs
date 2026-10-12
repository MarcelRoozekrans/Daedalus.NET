using System.Globalization;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>What kind of failure a GitHub call ended in. A consumer branches on this, never on the message.</summary>
public enum GitHubErrorKind
{
    /// <summary>GitHub refused for a rate limit: a 429, a 403 with <c>X-RateLimit-Remaining: 0</c>, or a 403 with <c>Retry-After</c>.</summary>
    RateLimited,

    /// <summary>404 or 410: the resource does not exist, or the token cannot see it.</summary>
    NotFound,

    /// <summary>401, or no token is configured.</summary>
    Unauthorized,

    /// <summary>A 403 that is not a rate limit.</summary>
    Forbidden,

    /// <summary>A 400, 422 or other 4xx, or a request Daedalus refused before sending it.</summary>
    Validation,

    /// <summary>409.</summary>
    Conflict,

    /// <summary>5xx.</summary>
    Server,

    /// <summary>The request failed before GitHub answered, or the resilience pipeline refused to send it.</summary>
    Transport,

    /// <summary>The request timed out, or GitHub answered 408.</summary>
    Timeout,

    /// <summary>Daedalus's own resilience pipeline refused the request before it was sent: an open circuit or a limiter.</summary>
    Rejected,

    /// <summary>GitHub's answer could not be read, or lacked a field Daedalus needs.</summary>
    Unreadable,
}

/// <summary>
///     A failed GitHub call. <see cref="Message"/> is built from the kind, the status and the retry time, or, for a refusal
///     Daedalus makes before sending, is Daedalus's own text. It never copies a response body or an exception's message:
///     GitHub's error text can quote what the request carried, and this message reaches agents, run errors and logs.
/// </summary>
/// <param name="Kind">What went wrong; the value to branch on.</param>
/// <param name="StatusCode">GitHub's status, or <see langword="null"/> when GitHub did not answer.</param>
/// <param name="RetryAt">When a rate limit lifts; set only for <see cref="GitHubErrorKind.RateLimited"/>.</param>
/// <param name="Message">One sentence for a person or an agent.</param>
public readonly record struct GitHubError(GitHubErrorKind Kind, int? StatusCode, DateTimeOffset? RetryAt, string Message)
{
    /// <summary>A refusal GitHub answered with <paramref name="status"/>. Only a rate limit keeps <paramref name="retryAt"/>.</summary>
    public static GitHubError FromStatus(int status, GitHubErrorKind kind, DateTimeOffset? retryAt)
    {
        var kept = kind == GitHubErrorKind.RateLimited ? retryAt : null;
        return new GitHubError(kind, status, kept, Describe(kind, status, kept));
    }

    /// <summary>A request Daedalus refused before sending it; <paramref name="message"/> is Daedalus's own text.</summary>
    public static GitHubError Refused(string message) => new(GitHubErrorKind.Validation, null, null, message);

    /// <summary>No token is configured; <paramref name="message"/> is the token source's text.</summary>
    public static GitHubError NoToken(string message) => new(GitHubErrorKind.Unauthorized, null, null, message);

    /// <summary>An answer of the wrong shape; <paramref name="message"/> is Daedalus's own text, never the answer.</summary>
    public static GitHubError Unreadable(string message, int? status = null) => new(GitHubErrorKind.Unreadable, status, null, message);

    /// <summary>The request timed out, on the client or in the resilience pipeline.</summary>
    public static GitHubError TimedOut() => new(GitHubErrorKind.Timeout, null, null, "The request to GitHub timed out.");

    /// <summary>The request failed before GitHub answered, or was not sent at all.</summary>
    public static GitHubError NotSent() => new(GitHubErrorKind.Transport, null, null, "The request to GitHub failed before GitHub answered.");

    /// <summary>
    ///     The resilience pipeline refused the request before it went out: an open circuit or a limiter. Unlike
    ///     <see cref="NotSent"/>, whose transport failure can follow a sent request, nothing reached GitHub, so
    ///     <see cref="ForWrite"/> adds no hint to this kind.
    /// </summary>
    public static GitHubError Rejected() => new(GitHubErrorKind.Rejected, null, null, RejectedMessage);

    /// <summary>The text of <see cref="Rejected"/>.</summary>
    public const string RejectedMessage = "The request to GitHub was not sent: Daedalus is holding back calls to GitHub after recent failures or too many calls. It can be tried again shortly.";

    /// <summary>The sentence <see cref="ForWrite"/> adds to a write that may have landed.</summary>
    public const string MayHaveLanded = "It may still have been applied: check before sending it again.";

    /// <summary>
    ///     GitHub's own words, summarised to one line of at most 300 characters, or <see langword="null"/>. For the agent
    ///     tools only, through <see cref="ForAgent"/>: it is never part of <see cref="Message"/> or <see cref="ToString"/>,
    ///     which reach run errors, logs and span status (spec amendment A2).
    /// </summary>
    public string? GitHubSaid { get; init; }

    /// <summary>The text an agent tool shows: the message, and what GitHub said when it said something.</summary>
    public string ForAgent() => GitHubSaid is { } said ? $"{Message} GitHub said: {said}" : Message;

    /// <summary>
    ///     The error of a write: a timeout, a server error, a transport failure or an answer that could not be read may have
    ///     happened after GitHub applied the write, so the message says to check before sending it again. GitHub has no idempotency keys.
    /// </summary>
    public GitHubError ForWrite() =>
        Kind is GitHubErrorKind.Timeout or GitHubErrorKind.Server or GitHubErrorKind.Transport or GitHubErrorKind.Unreadable
            ? this with { Message = $"{Message} {MayHaveLanded}" }
            : this;

    /// <inheritdoc />
    public override string ToString() => Message ?? string.Empty;

    private static string Describe(GitHubErrorKind kind, int status, DateTimeOffset? retryAt) => kind switch
    {
        GitHubErrorKind.RateLimited when retryAt is { } at =>
            $"GitHub answered {status}: rate-limited until {at.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC.",
        GitHubErrorKind.RateLimited => $"GitHub answered {status}: rate-limited, and it gave no time to retry.",
        GitHubErrorKind.NotFound => $"GitHub answered {status}: it does not exist, or the configured token cannot see it.",
        GitHubErrorKind.Unauthorized => $"GitHub answered {status}: the configured token was not accepted.",
        GitHubErrorKind.Forbidden => $"GitHub answered {status}: the configured token may not do this.",
        GitHubErrorKind.Validation => $"GitHub answered {status}: it refused the request as invalid.",
        GitHubErrorKind.Conflict => $"GitHub answered {status}: the request conflicts with the repository's current state.",
        GitHubErrorKind.Timeout => $"GitHub answered {status}: the request timed out.",
        GitHubErrorKind.Rejected => RejectedMessage,
        _ => $"GitHub answered {status}: a server error.",
    };
}
