using Daedalus.Web.Auth;
using Daedalus.Web.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Radzen;

namespace Daedalus.Tests.Unit.Web;

/// <summary>An ended session raises no Error notification of a caller's own; any other failure still does.</summary>
public sealed class FailureNoticesTests
{
    private readonly NotificationService _notifications = new();

    /// <summary>
    ///     The signing-in failure notifies nothing.
    ///     Red: drop the IsSigningIn check from NotifyFailure's string overload; one Error message is recorded.
    /// </summary>
    [Fact]
    public void The_signing_in_failure_notifies_nothing()
    {
        _notifications.NotifyFailure("Error", SessionExpiry.SigningInAgain);

        _notifications.Messages.Should().BeEmpty();
    }

    /// <summary>
    ///     A request that failed because its session ended notifies nothing.
    ///     Red: drop the IsSigningIn check from NotifyFailure's exception overload; one Error message is recorded.
    /// </summary>
    [Fact]
    public void A_session_that_ended_notifies_nothing()
    {
        var navigation = new RecordingNavigation();

        _notifications.NotifyFailure(
            "Error", new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null), "Failed to create.");

        _notifications.Messages.Should().BeEmpty();
    }

    /// <summary>
    ///     Any other failure is one Error notification with its text.
    ///     Red: make NotifyFailure notify nothing; no message is recorded.
    ///     Red: notify at Warning; the severity differs.
    /// </summary>
    [Fact]
    public void Another_failure_is_an_error_notification()
    {
        _notifications.NotifyFailure("Error", "Title is required.");
        _notifications.NotifyFailure("Error", new HttpRequestException("refused"), "Failed to create.");

        _notifications.Messages.Select(m => (m.Severity, m.Detail)).Should().Equal(
            (NotificationSeverity.Error, "Title is required."),
            (NotificationSeverity.Error, "Failed to create."));
    }
}
