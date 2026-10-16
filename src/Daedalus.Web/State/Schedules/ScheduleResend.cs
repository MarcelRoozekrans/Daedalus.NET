namespace Daedalus.Web.State.Schedules;

/// <summary>
///     The outcome of a resend: requeued when <paramref name="Refusal"/> is <see langword="null"/>, refused otherwise, with
///     the API's reason. The page shows it under the run it resent; the effect never does, and it is not state.
/// </summary>
public readonly record struct ScheduleResend(string? Refusal)
{
    /// <summary>The outcome of a resend the API accepted.</summary>
    public static ScheduleResend Requeued { get; } = new(null);

    /// <summary>Whether the API accepted the resend: the message is queued again.</summary>
    public bool Succeeded => Refusal is null;
}
