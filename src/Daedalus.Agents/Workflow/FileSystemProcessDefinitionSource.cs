using Thalos.Workflow;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Reads process definitions from <c>*.yaml</c> files in <paramref name="root"/> — the working copy on disk,
///     not a git operation of any kind (no clone, fetch, or diff): this type only calls
///     <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> against whatever directory the host
///     hands it. "Where the files come from" (checked into the same repository as this source, deployed
///     alongside it) is host policy, per <see cref="IProcessDefinitionSource"/>'s own remarks — activation,
///     pinning and retention all live in Thalos's <see cref="ProcessDefinitionSync"/> instead.
/// </summary>
/// <remarks>
///     <para>
///     A missing <paramref name="root"/> is not fatal: a host with no process files authored yet — or one whose
///     <c>processes</c> folder has not been copied to this deployment layout yet — should still boot and sync
///     zero definitions, the same way <see cref="ProcessDefinitionSync.SyncAsync"/> already tolerates an empty
///     source. Root resolution (relative paths against the content root, falling back to the assembly directory)
///     is the caller's job — see <c>DaedalusAgentsServiceCollectionExtensions.ResolveContentRoot</c>, reused here
///     for the same reason it exists for <c>Thalos:Skills:Roots</c>.
///     </para>
///     <para>
///     <b>Recursive, deliberately, and it has to match the build.</b> <c>Daedalus.Api.csproj</c> deploys
///     <c>..\..\processes\**\*.yaml</c> with <c>%(RecursiveDir)</c> preserved, so a file authored at
///     <c>processes/sub/thing.yaml</c> lands at <c>processes/sub/thing.yaml</c> next to the API. Enumerating
///     <see cref="SearchOption.TopDirectoryOnly"/> here would mean that file deploys perfectly and then never
///     loads, with nothing logged and nothing failing — the "declared, never wired" shape Task 11 was fixing.
///     Of the two ways to make the copy rule and the read rule agree, this is the one where no authored file is
///     ever silently ignored: narrowing the glob instead would have left a nested file undeployed, which is just
///     as silent. Reading more than the build deploys is not a risk either, because the same
///     <c>%(RecursiveDir)</c> glob is what filled the directory being read. Two files under different
///     subdirectories declaring the same <c>process:</c> name is the one hazard recursion adds, and it fails
///     loudly rather than silently: <see cref="ProcessDefinitionSync.SyncAsync"/> rejects the whole batch by
///     name rather than letting listing order decide which one activates.
///     </para>
/// </remarks>
internal sealed class FileSystemProcessDefinitionSource(string root) : IProcessDefinitionSource
{
    public async ValueTask<IReadOnlyList<ProcessDocument>> ReadAllAsync(CancellationToken ct)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var documents = new List<ProcessDocument>();
        foreach (var path in Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories))
        {
            documents.Add(new ProcessDocument(path, await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)));
        }

        return documents;
    }
}
