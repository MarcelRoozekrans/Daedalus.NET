using Daedalus.Web.Auth;
using Radzen;

namespace Daedalus.Web.Services;

/// <summary>
///     Error notifications that stay quiet for a session that has ended: the layout already says "Signing in again", and
///     an Error toast for a request that only failed because of that would claim a fault there is not.
/// </summary>
public static class FailureNotices
{
    /// <summary>Notifies <paramref name="error"/> at Error level, unless it is <see cref="SessionExpiry.SigningInAgain"/>.</summary>
    public static void NotifyFailure(this NotificationService notifications, string summary, string? error)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        if (!SessionExpiry.IsSigningIn(error))
        {
            notifications.Notify(NotificationSeverity.Error, summary, error ?? string.Empty);
        }
    }

    /// <summary>Notifies <paramref name="detail"/> at Error level, unless <paramref name="exception"/> ended the session.</summary>
    public static void NotifyFailure(this NotificationService notifications, string summary, Exception exception, string detail)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        if (!SessionExpiry.IsSigningIn(exception))
        {
            notifications.Notify(NotificationSeverity.Error, summary, detail);
        }
    }
}
