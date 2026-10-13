using Radzen;

namespace Daedalus.Web.Services;

/// <summary>The UI contract's status mapping, the one place a run status or a step becomes text and a severity.</summary>
public static class RunStatusLabels
{
    /// <summary>The badge text of a run status; a parked run names what it waits for.</summary>
    public static string Text(string status, string? awaitingSignal) => status switch
    {
        "Awaiting" when string.Equals(awaitingSignal, RunSignals.HumanApproval, StringComparison.Ordinal) => "Awaiting approval",
        "Awaiting" => $"Waiting: {awaitingSignal}",
        _ => status,
    };

    /// <summary>The badge severity of a run status. The signal does not change it; it keeps the call uniform with <see cref="Text"/>.</summary>
    public static BadgeStyle Style(string status, string? awaitingSignal) => status switch
    {
        "Running" => BadgeStyle.Info,
        "Awaiting" => BadgeStyle.Warning,
        "Succeeded" => BadgeStyle.Success,
        "Failed" => BadgeStyle.Danger,
        _ => BadgeStyle.Light,
    };

    /// <summary>
    ///     The badge text of a timeline step. A kind that carries the step's meaning, Failed, CapExceeded, Cancelled,
    ///     Resumed or Retried, shows as itself whatever outcome it holds; any other step shows the outcome a
    ///     completion reported, else the event kind.
    /// </summary>
    public static string StepText(string kind, string? outcome) => IsSignificantKind(kind) ? kind : outcome ?? kind;

    /// <summary>The badge severity of a timeline step.</summary>
    public static BadgeStyle StepStyle(string kind, string? outcome) => (kind, outcome) switch
    {
        ("Failed" or "CapExceeded", _) => BadgeStyle.Danger,
        ("Resumed" or "Retried", _) => BadgeStyle.Info,
        ("Cancelled", _) => BadgeStyle.Light,
        (_, "approved" or "published") => BadgeStyle.Success,
        (_, "rejected") => BadgeStyle.Danger,
        _ => BadgeStyle.Light,
    };

    private static bool IsSignificantKind(string kind) =>
        kind is "Failed" or "CapExceeded" or "Cancelled" or "Resumed" or "Retried";
}

/// <summary>The signal the manufacture process's human gate awaits.</summary>
public static class RunSignals
{
    /// <summary>The human approval gate's signal.</summary>
    public const string HumanApproval = "human_approval";
}
