namespace Daedalus.Agents;

/// <summary>
///     One entry of <c>Thalos:Workflow:WriteGrants</c>: the process node whose turn may write into its run's workspace.
///     Kept in deploy-reviewed configuration on purpose, never in the process YAML, because a grant there would widen a
///     capability live under interval resync.
/// </summary>
public sealed class WriteGrantConfig
{
    /// <summary>The process name, for example <c>manufacture</c>. Required.</summary>
    public string Process { get; set; } = "";

    /// <summary>The node id within <see cref="Process"/>, for example <c>implement</c>. Required.</summary>
    public string Node { get; set; } = "";

    /// <summary>
    ///     The file extensions this grant may write, each with its leading dot, for example <c>.cs</c> (ruling R29). An
    ///     allow-list, never a deny-list. Empty after binding means the key is missing, which
    ///     <c>ValidateWorkflowWriteConfig</c> rejects: absence is not a supported configuration. Empty by default
    ///     because the binder appends to a pre-filled list.
    /// </summary>
    public IList<string> AllowedExtensions { get; } = [];
}
