namespace Daedalus.Agents;

/// <summary>
///     <c>Thalos:Workflow:CommitAuthor</c>: the git author every manufacture-run commit is written as. Both fields are
///     required whenever <see cref="WorkflowConfig.Repositories"/> is non-empty.
/// </summary>
public sealed class CommitAuthorConfig
{
    /// <summary>The commit author's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The commit author's email address.</summary>
    public string Email { get; set; } = "";
}
