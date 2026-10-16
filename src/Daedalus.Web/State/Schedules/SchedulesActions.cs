using Daedalus.Application.DTOs.Scheduling;

namespace Daedalus.Web.State.Schedules;

/// <summary>A read of the overview was started, numbered <paramref name="Sequence"/>.</summary>
public readonly record struct SchedulesLoadStarted(long Sequence);

/// <summary>Overview read <paramref name="Sequence"/> answered with <paramref name="Overview"/>.</summary>
public readonly record struct SchedulesLoaded(long Sequence, IReadOnlyList<RunDiagnosis> Overview);

/// <summary>Overview read <paramref name="Sequence"/> failed with <paramref name="Error"/>.</summary>
public readonly record struct SchedulesLoadFailed(long Sequence, string Error);

/// <summary>A read of schedule <paramref name="ScheduleId"/>'s run history was started, numbered <paramref name="Sequence"/>.</summary>
public readonly record struct ScheduleHistoryStarted(Guid ScheduleId, long Sequence);

/// <summary>History read <paramref name="Sequence"/> of schedule <paramref name="ScheduleId"/> answered with <paramref name="Runs"/>.</summary>
public readonly record struct ScheduleHistoryLoaded(Guid ScheduleId, long Sequence, IReadOnlyList<RunDiagnosis> Runs);

/// <summary>History read <paramref name="Sequence"/> of schedule <paramref name="ScheduleId"/> failed with <paramref name="Error"/>.</summary>
public readonly record struct ScheduleHistoryFailed(Guid ScheduleId, long Sequence, string Error);

/// <summary>Run <paramref name="Run"/> was selected in schedule <paramref name="ScheduleId"/>'s history: its detail shows.</summary>
public readonly record struct ScheduleRunSelected(Guid ScheduleId, RunDiagnosis Run);

/// <summary>The Schedules page closed: what it read goes.</summary>
public readonly record struct SchedulesClosed;
