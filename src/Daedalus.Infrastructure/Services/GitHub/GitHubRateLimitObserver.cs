using System.Diagnostics.Metrics;
using System.Globalization;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>
///     Reports how much of GitHub's rate limit is left, from every answer's <c>X-RateLimit-Remaining</c>, as the gauge
///     <see cref="RemainingName"/>, tagged with the limit's resource, such as <c>core</c> or <c>search</c>. It sits inside
///     the retries, so every attempt counts, and below the conditional cache, so a <c>304</c> is seen too. It measures and
///     never throttles (spec D5).
/// </summary>
/// <remarks>
///     <b>No content.</b> The only tag is GitHub's resource name: no path, repository, token or status. The meter is under
///     <c>daedalus.*</c>, which the service defaults collect.
/// </remarks>
public sealed class GitHubRateLimitObserver : DelegatingHandler
{
    /// <summary>The meter, under the <c>daedalus.*</c> wildcard the service defaults subscribe to.</summary>
    public const string MeterName = "daedalus.github";

    /// <summary>The gauge's name.</summary>
    public const string RemainingName = "daedalus.github.ratelimit.remaining";

    /// <summary>The one tag: GitHub's <c>X-RateLimit-Resource</c>, or <c>unknown</c>.</summary>
    public const string ResourceTag = "daedalus.github.resource";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Gauge<long> Remaining = Meter.CreateGauge<long>(
        RemainingName, unit: "{request}", description: "Requests left in the current GitHub rate-limit window, per resource");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Record(response);
        return response;
    }

    private static void Record(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            || !long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var remaining))
        {
            return;
        }

        var resource = response.Headers.TryGetValues("X-RateLimit-Resource", out var names) ? names.FirstOrDefault() : null;
        Remaining.Record(remaining, new KeyValuePair<string, object?>(ResourceTag, string.IsNullOrWhiteSpace(resource) ? "unknown" : resource));
    }
}
