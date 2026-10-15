using System.Globalization;

namespace Daedalus.Web.Services;

/// <summary>Server times, which are UTC, as the viewer reads them.</summary>
public static class LocalTimes
{
    /// <summary><paramref name="utc"/> in the viewer's time zone, in the current culture's short date and time.</summary>
    public static string ShortDateTime(DateTime utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
