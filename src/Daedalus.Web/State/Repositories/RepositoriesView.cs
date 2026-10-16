using Radzen;

namespace Daedalus.Web.State.Repositories;

/// <summary>A notification the page shows for a write's outcome.</summary>
public readonly record struct RepositoryNotice(NotificationSeverity Severity, string Summary, string Detail);

/// <summary>
///     What the Git Repositories page shows for a write's outcome, word for word as it always did, and whether it reloads
///     the list after it. Pure: the page passes the outcome its write returned.
/// </summary>
public static class RepositoriesView
{
    /// <summary>
    ///     The notification for <paramref name="save"/>. A refused write says what failed and the status the server
    ///     answered; one that got no answer says only what failed. A connection test the server refused is a Warning; one
    ///     that got no answer is an Error. The effect never stores a session being renewed, so no outcome is that.
    /// </summary>
    public static RepositoryNotice Notice(RepositorySave save) => save switch
    {
        { Succeeded: true } => save.Write switch
        {
            RepositoryWrite.Created => new(NotificationSeverity.Success, "Created", "Repository added successfully."),
            RepositoryWrite.Updated => new(NotificationSeverity.Success, "Updated", "Repository updated successfully."),
            RepositoryWrite.Deleted => new(NotificationSeverity.Success, "Deleted", "Repository removed."),
            _ => new(NotificationSeverity.Success, "Success", "Connection test passed."),
        },
        { Write: RepositoryWrite.Tested, Answered: true } =>
            new(NotificationSeverity.Warning, "Failed", "Connection test failed."),
        { Write: RepositoryWrite.Tested } => new(NotificationSeverity.Error, "Error", "Could not test connection."),
        { Answered: true } => new(NotificationSeverity.Error, "Error", $"{Failed(save.Write)} {save.Refusal}"),
        _ => new(NotificationSeverity.Error, "Error", Failed(save.Write)),
    };

    /// <summary>
    ///     Whether the page reloads the list after <paramref name="save"/>: after a write the server answered, saved or
    ///     refused, and not after one that got no answer, nor after a connection test.
    /// </summary>
    public static bool Reloads(RepositorySave save) => save.Write != RepositoryWrite.Tested && save.Answered;

    private static string Failed(RepositoryWrite write) => write switch
    {
        RepositoryWrite.Created => "Failed to create repository.",
        RepositoryWrite.Updated => "Failed to update repository.",
        _ => "Failed to delete repository.",
    };
}
