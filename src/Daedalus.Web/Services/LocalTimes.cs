using System.Globalization;

namespace Daedalus.Web.Services;

/// <summary>Server times, which are UTC, as the viewer reads them.</summary>
public static class LocalTimes
{
    /// <summary><paramref name="utc"/> in the viewer's time zone, in the current culture's short date and time.</summary>
    public static string ShortDateTime(DateTime utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>
    ///     How long it has been from <paramref name="since"/> to <paramref name="now"/>, in the largest whole unit: "less than
    ///     a minute", "N min", "N h" below two days, then "N d". A <paramref name="since"/> after <paramref name="now"/>, a
    ///     viewer's clock behind the server's, reads as less than a minute.
    /// </summary>
    public static string Elapsed(DateTimeOffset since, DateTimeOffset now)
    {
        var elapsed = now - since;
        return elapsed switch
        {
            { TotalMinutes: < 1 } => "less than a minute",
            { TotalHours: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes} min"),
            { TotalDays: < 2 } => string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours} h"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalDays} d"),
        };
    }
}
