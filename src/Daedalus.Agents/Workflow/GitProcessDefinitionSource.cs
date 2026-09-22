using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Reads process definitions from <c>*.yaml</c> files in <paramref name="root"/> — the only part of
///     definition handling that is host policy per <see cref="IProcessDefinitionSource"/>'s own remarks;
///     activation, pinning and retention all live in Thalos's <see cref="ProcessDefinitionSync"/>.
/// </summary>
/// <remarks>
///     A missing <paramref name="root"/> is not fatal: a host with no process files authored yet — or one whose
///     <c>processes</c> folder has not been copied to this deployment layout yet — should still boot and sync
///     zero definitions, the same way <see cref="ProcessDefinitionSync.SyncAsync"/> already tolerates an empty
///     source. Root resolution (relative paths against the content root, falling back to the assembly directory)
///     is the caller's job — see <c>DaedalusAgentsServiceCollectionExtensions.ResolveSkillRoot</c>, reused here
///     for the same reason it exists for <c>Thalos:Skills:Roots</c>.
/// </remarks>
internal sealed class GitProcessDefinitionSource(string root) : IProcessDefinitionSource
{
    public async ValueTask<IReadOnlyList<ProcessDocument>> ReadAllAsync(CancellationToken ct)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var documents = new List<ProcessDocument>();
        foreach (var path in Directory.EnumerateFiles(root, "*.yaml", SearchOption.TopDirectoryOnly))
        {
            documents.Add(new ProcessDocument(path, await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)));
        }

        return documents;
    }
}
