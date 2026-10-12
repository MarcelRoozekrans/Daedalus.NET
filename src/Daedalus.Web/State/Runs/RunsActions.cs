using Daedalus.Application.DTOs.WorkflowRuns;

namespace Daedalus.Web.State.Runs;

/// <summary>A list page was asked for.</summary>
public readonly record struct RunListRequested(RunListQuery Query);

/// <summary>A list page arrived for <paramref name="Query"/>.</summary>
public readonly record struct RunListLoaded(RunListQuery Query, WorkflowRunList List);

/// <summary>The list page for <paramref name="Query"/> could not be fetched.</summary>
public readonly record struct RunListFailed(RunListQuery Query, string Error);

/// <summary>The operator moved to the page after this one.</summary>
public readonly record struct RunListPagedForward(string Next);

/// <summary>The operator moved back to the page before this one.</summary>
public readonly record struct RunListPagedBack;

/// <summary>
///     The list page was left. Its rows are dropped, so a change no longer re-fetches a list nobody sees; the query is
///     kept, and the page reloads its own on return.
/// </summary>
public readonly record struct RunListClosed;

/// <summary>The open run was left, so a change no longer re-fetches a run nobody sees.</summary>
public readonly record struct RunClosed;

/// <summary>A run was opened.</summary>
public readonly record struct RunSelected(Guid Id);

/// <summary>The open run is being fetched.</summary>
public readonly record struct RunLoadStarted(Guid Id);

/// <summary>A run arrived.</summary>
public readonly record struct RunLoaded(WorkflowRunView Run);

/// <summary>The server said the run does not exist.</summary>
public readonly record struct RunNotFound(Guid Id);

/// <summary>The run could not be fetched.</summary>
public readonly record struct RunLoadFailed(Guid Id, string Error);

/// <summary>The operator marked a deferred finding as dropped, or took the mark back.</summary>
public readonly record struct GateFindingToggled(string FindingId, bool Dropped);

/// <summary>The operator chose whether to accept the proposed standing instructions.</summary>
public readonly record struct GateApplyToggled(bool Apply);

/// <summary>The approval was sent.</summary>
public readonly record struct GateSubmitStarted;

/// <summary>The approval was accepted.</summary>
public readonly record struct GateSubmitSucceeded;

/// <summary>The approval was refused.</summary>
public readonly record struct GateSubmitFailed(string Error);

/// <summary>The awaiting-approval count arrived.</summary>
public readonly record struct AwaitingApprovalLoaded(int Count, bool More);

/// <summary>The live connection changed state.</summary>
public readonly record struct HubStatusChanged(HubStatus Status);
