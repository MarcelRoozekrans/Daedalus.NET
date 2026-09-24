namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     A scratch directory under <see cref="global::System.IO.Path.GetTempPath"/>, deleted recursively on
///     dispose. Test isolation for anything that reads or writes a standing-instructions file: a test must never
///     touch <c>AGENT.md</c> — or any file — under a real project directory, only a throwaway one of its own.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    private readonly string _root = global::System.IO.Path.Combine(
        global::System.IO.Path.GetTempPath(), $"daedalus-standing-instructions-{Guid.NewGuid():N}");

    public TempDirectory() => Directory.CreateDirectory(_root);

    /// <summary>Combines <paramref name="relative"/> onto this directory's root. Named to match test call sites: <c>dir.Path("AGENT.md")</c>.</summary>
    public string Path(string relative) => global::System.IO.Path.Combine(_root, relative);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
