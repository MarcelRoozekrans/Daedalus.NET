namespace Daedalus.Infrastructure.Agents.Tools;

/// <summary>
///     The notice on the knowledge tools whose data only the retired task loop wrote (phase 2.8, D3 and A5). Rebuilding
///     that data from review rejections is tracked as an issue.
/// </summary>
internal static class FrozenHistory
{
    /// <summary>Said in each tool's description and in an empty answer, so a model does not read absence as evidence.</summary>
    public const string Notice =
        "This data was written only by the autonomous task loop that Daedalus retired in phase 2.8 (October 2026), and " +
        "nothing adds to it any more: an empty answer does not mean that no such pattern or learning exists.";
}
