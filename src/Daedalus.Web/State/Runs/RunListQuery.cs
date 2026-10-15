using System.Globalization;

namespace Daedalus.Web.State.Runs;

/// <summary>Which page of runs the list shows.</summary>
/// <param name="Status">The status filter, or <see langword="null"/> for every status.</param>
/// <param name="Process">The process filter, or <see langword="null"/> for every process.</param>
/// <param name="After">The cursor of the page, or <see langword="null"/> for the first page.</param>
/// <param name="Take">How many runs one page holds.</param>
/// <param name="Signal">Only runs awaiting this signal, or <see langword="null"/> for any.</param>
/// <param name="UpdatedSince">Only runs that last changed at or after this instant, or <see langword="null"/> for any.</param>
public sealed record RunListQuery(
    string? Status, string? Process, string? After, int Take = RunListQuery.PageSize, string? Signal = null,
    DateTimeOffset? UpdatedSince = null)
{
    /// <summary>The contract's page size.</summary>
    public const int PageSize = 15;

    /// <summary>The first page, unfiltered.</summary>
    public static RunListQuery First { get; } = new(null, null, null);

    /// <summary>The list route with this query: <c>take</c> always, the rest only when set, each escaped.</summary>
    public string ToPath()
    {
        var parts = new List<string> { $"take={Take}" };
        if (Status is { Length: > 0 })
        {
            parts.Add($"status={Uri.EscapeDataString(Status)}");
        }

        if (Process is { Length: > 0 })
        {
            parts.Add($"process={Uri.EscapeDataString(Process)}");
        }

        if (After is { Length: > 0 })
        {
            parts.Add($"after={Uri.EscapeDataString(After)}");
        }

        if (Signal is { Length: > 0 })
        {
            parts.Add($"signal={Uri.EscapeDataString(Signal)}");
        }

        if (UpdatedSince is { } since)
        {
            parts.Add($"updatedSince={Uri.EscapeDataString(since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}");
        }

        return "/api/workflow-runs?" + string.Join('&', parts);
    }
}
