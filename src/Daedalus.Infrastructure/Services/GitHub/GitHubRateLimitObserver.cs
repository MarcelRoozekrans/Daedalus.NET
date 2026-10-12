using System.Collections.Frozen;
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
///     <c>daedalus.*</c>, which the service defaults collect. The header is not copied: an upstream such as GitHub
///     Enterprise Server or a proxy can send any text, and an exported tag with unbounded values makes unbounded series.
///     So the value is mapped through the resources GitHub documents (<see cref="Resources"/>), compared ordinally,
///     because GitHub sends them in lowercase and anything else is not a documented name. Any other value is tagged
///     <c>other</c>; a missing or blank header is tagged <c>unknown</c>.
/// </remarks>
public sealed class GitHubRateLimitObserver : DelegatingHandler
{
    /// <summary>The meter, under the <c>daedalus.*</c> wildcard the service defaults subscribe to.</summary>
    public const string MeterName = "daedalus.github";

    /// <summary>The gauge's name.</summary>
    public const string RemainingName = "daedalus.github.ratelimit.remaining";

    /// <summary>
    ///     The one tag: GitHub's <c>X-RateLimit-Resource</c> when it is one of the documented names in <see cref="Resources"/>,
    ///     <c>other</c> for any undocumented value, or <c>unknown</c> when the header is missing or blank.
    /// </summary>
    public const string ResourceTag = "daedalus.github.resource";

    private static readonly FrozenSet<string> Resources = new[]
    {
        "core", "search", "code_search", "graphql", "integration_manifest", "source_import", "code_scanning_upload",
        "code_scanning_autofix", "actions_runner_registration", "scim", "dependency_snapshots", "dependency_sbom",
        "audit_log", "audit_log_streaming",
    }.ToFrozenSet(StringComparer.Ordinal);

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
        Remaining.Record(remaining, new KeyValuePair<string, object?>(ResourceTag, Label(resource)));
    }

    private static string Label(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
            return "unknown";

        return Resources.Contains(resource) ? resource : "other";
    }
}
