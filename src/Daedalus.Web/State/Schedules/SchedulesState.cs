using System.Collections.Immutable;
using Daedalus.Application.DTOs.Scheduling;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Schedules;

/// <summary>One schedule's run history, as its expanded row shows it.</summary>
/// <param name="Runs">
///     The runs, or <see langword="null"/> while the read is in flight, after it failed, and after a read that ended with
///     the session being renewed: the row keeps its spinner, as the layout says so.
/// </param>
/// <param name="Error">Why the read failed, or <see langword="null"/>. Never a session being renewed or a cancellation.</param>
/// <param name="Selected">The run whose detail shows under the history, or <see langword="null"/>.</param>
/// <param name="Sequence">The number of the history's newest read; an answer to any other is dropped.</param>
public readonly record struct ScheduleHistory(IReadOnlyList<RunDiagnosis>? Runs, string? Error, RunDiagnosis? Selected, long Sequence)
{
    /// <summary>Whether the read is in flight: the row shows "Loading run history…".</summary>
    public bool Loading => Runs is null && Error is null;
}

/// <summary>
///     The Schedules page: the overview of every schedule's last run, and each expanded schedule's run history with the
///     run selected in it. A resend's outcome is not state: the resend returns it to the page, which shows it under the
///     run it asked for. The deep link, the focus and the mobile cards' open details are the page's own.
///     <para>
///         Lifetime: page-only, cleared by <see cref="SchedulesClosed"/> when the page is disposed. Only that page reads
///         or loads it, so its clear races no other reader. <paramref name="Sequence"/> survives the clear, so an answer
///         to a read made before it never lands in a read made after it.
///     </para>
/// </summary>
/// <param name="Overview">
///     Every schedule's last run, or <see langword="null"/> while the read is in flight, after it failed, and after a
///     read that ended with the session being renewed.
/// </param>
/// <param name="Error">
///     Why the overview failed to load, or <see langword="null"/>. Never <see cref="Auth.SessionExpiry.SigningInAgain"/>,
///     and never a cancellation: neither is stored.
/// </param>
/// <param name="OverviewSequence">The number of the overview's newest read; an answer to any other is dropped.</param>
/// <param name="Histories">Each expanded schedule's run history, by schedule id; a schedule is read once.</param>
/// <param name="Sequence">The number of the newest read of any kind started, never reset.</param>
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct SchedulesState(
    IReadOnlyList<RunDiagnosis>? Overview,
    string? Error,
    long OverviewSequence,
    ImmutableDictionary<Guid, ScheduleHistory> Histories,
    long Sequence)
{
    /// <summary>The page with nothing read, numbered from the start.</summary>
    public static SchedulesState Empty { get; } = new(null, null, 0, ImmutableDictionary<Guid, ScheduleHistory>.Empty, 0);

    /// <summary>The state before any read.</summary>
    public static SchedulesState Initial(IServiceProvider services) => Empty;
}
