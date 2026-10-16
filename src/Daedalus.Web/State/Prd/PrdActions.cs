namespace Daedalus.Web.State.Prd;

/// <summary>Project <paramref name="ProjectId"/> was picked: the wizard asks for the requirements.</summary>
public readonly record struct PrdProjectSelected(Guid ProjectId);

/// <summary>A generate or a convert was started, numbered <paramref name="Sequence"/>.</summary>
public readonly record struct PrdRequestStarted(long Sequence);

/// <summary>Generate <paramref name="Sequence"/> answered with <paramref name="Prd"/>.</summary>
public readonly record struct PrdGenerated(long Sequence, PrdResponseDto Prd);

/// <summary>Convert <paramref name="Sequence"/> created <paramref name="Tasks"/>.</summary>
public readonly record struct PrdConverted(long Sequence, IReadOnlyList<TaskDto> Tasks);

/// <summary>Request <paramref name="Sequence"/>, a generate or a convert, failed with <paramref name="Error"/>.</summary>
public readonly record struct PrdRequestFailed(long Sequence, string Error);

/// <summary>The person closed the failure's alert.</summary>
public readonly record struct PrdErrorDismissed;

/// <summary>The wizard starts over: Cancel, or Create Another PRD.</summary>
public readonly record struct PrdReset;

/// <summary>The PRD generator page closed: what it held goes.</summary>
public readonly record struct PrdClosed;
