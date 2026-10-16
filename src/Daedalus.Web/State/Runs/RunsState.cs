using Daedalus.Application.DTOs.WorkflowRuns;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Runs;

/// <summary>
///     Everything the console shows about runs: the list page, the open run, its gate draft, the count and the live link.
///     <para>
///         Lifetime: the list is page-only, cleared by <see cref="RunListClosed"/>, and the open run with its gate draft
///         by <see cref="RunClosed"/>. The awaiting-approval count and the hub status are read by the menu and the live
///         indicator on every page, and live as long as the app. The query is kept, so the list opens again with the
///         same filters, on its first page.
///     </para>
/// </summary>
/// <param name="Query">The list page asked for last.</param>
/// <param name="PreviousCursors">The cursors of the pages before this one, so Back can return to them.</param>
/// <param name="List">The list page, or <see langword="null"/> before one has loaded.</param>
/// <param name="ListLoading">Whether a list page is being fetched.</param>
/// <param name="ListError">
///     Why the last list fetch failed, or <see langword="null"/>. Never
///     <see cref="Auth.SessionExpiry.SigningInAgain"/>, and never a failure that came back after its caller went away.
/// </param>
/// <param name="SelectedId">The open run's id, or <see langword="null"/> when none is open.</param>
/// <param name="Selected">The open run, or <see langword="null"/> before it has loaded.</param>
/// <param name="SelectedLoading">Whether the open run is being fetched for the first time.</param>
/// <param name="SelectedNotFound">Whether the server said the open run does not exist.</param>
/// <param name="SelectedForbidden">Whether the server refused the open run to this user, a 403.</param>
/// <param name="SelectedError">
///     Why the last fetch of the open run failed, or <see langword="null"/>. Never
///     <see cref="Auth.SessionExpiry.SigningInAgain"/>, and never a failure that came back after its caller went away.
/// </param>
/// <param name="Gate">The operator's gate draft for the open run.</param>
/// <param name="AwaitingApproval">How many runs await human approval, up to one page of 100.</param>
/// <param name="AwaitingApprovalMore">Whether more runs await than that page holds.</param>
/// <param name="Hub">The state of the live connection.</param>
/// <param name="ListRequested">
///     Whether the list page has asked for a page since it was last closed, whatever came of it. A change and a
///     reconnect re-fetch the list on it, so a list whose load failed refreshes too; a late page or failure that
///     lands after the close is ignored on it.
/// </param>
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct RunsState(
    RunListQuery Query,
    IReadOnlyList<string?> PreviousCursors,
    WorkflowRunList? List,
    bool ListLoading,
    string? ListError,
    Guid? SelectedId,
    WorkflowRunView? Selected,
    bool SelectedLoading,
    bool SelectedNotFound,
    bool SelectedForbidden,
    string? SelectedError,
    GateDraft Gate,
    int AwaitingApproval,
    bool AwaitingApprovalMore,
    HubStatus Hub,
    bool ListRequested)
{
    /// <summary>
    ///     The state before anything has loaded: the first page, no run open, and the live connection still being made.
    /// </summary>
    public static RunsState Initial(IServiceProvider services) =>
        new(RunListQuery.First, [], null, false, null, null, null, false, false, false, null, GateDraft.Empty, 0, false, HubStatus.Connecting,
            ListRequested: false);
}

/// <summary>What the operator has chosen at the gate but not yet sent.</summary>
/// <param name="Dropped">The ids of the deferred findings the operator decided not to file.</param>
/// <param name="ApplyStandingInstructions">Whether to accept the run's proposed standing instructions.</param>
/// <param name="Submitting">
///     Whether an approval is in flight or was accepted and the run has not yet left the gate. It stays set after
///     success, so Approve and Cancel stay busy until a fetch shows the run off the gate; the draft then resets.
/// </param>
/// <param name="Error">
///     Why the last approval was refused, or <see langword="null"/>. Never
///     <see cref="Auth.SessionExpiry.SigningInAgain"/>, and never a failure that came back after its caller went away.
/// </param>
public sealed record GateDraft(IReadOnlySet<string> Dropped, bool ApplyStandingInstructions, bool Submitting, string? Error)
{
    /// <summary>A draft with nothing chosen.</summary>
    public static GateDraft Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal), false, false, null);
}

/// <summary>The state of the live connection, as the LiveIndicator shows it.</summary>
public enum HubStatus
{
    /// <summary>The first connection is still being made; nothing has failed yet.</summary>
    Connecting,

    /// <summary>Connected; changes arrive as they happen.</summary>
    Connected,

    /// <summary>The connection dropped and is being re-established.</summary>
    Reconnecting,

    /// <summary>Not connected; pages load only on navigation.</summary>
    Disconnected,
}
