namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     A scratch directory under <see cref="global::System.IO.Path.GetTempPath"/>, deleted recursively on
///     dispose. Test isolation for anything that reads or writes a standing-instructions file: a test must never
///     touch <c>AGENT.md</c> — or any file — under a real project directory, only a throwaway one of its own.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    private readonly string _root;

    public TempDirectory()
        : this(global::System.IO.Path.Combine(
            global::System.IO.Path.GetTempPath(), $"daedalus-standing-instructions-{Guid.NewGuid():N}"))
    {
    }

    /// <summary>A scratch directory at exactly <paramref name="root"/>, created now and deleted on dispose.</summary>
    public TempDirectory(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    ///     A fresh path, relative to a host's content root, for a booted host's standing-instructions file.
    ///     <c>AddDaedalusAgents</c> refuses a <c>Thalos:Workflow:StandingInstructionsPath</c> outside the content
    ///     root, and <c>ApiWebApplicationFactory</c>'s content root is the real <c>src/Daedalus.Api</c> project
    ///     directory. So a test puts its file under that project's <c>obj</c> folder, which git ignores and the
    ///     build owns, in a directory of its own that <see cref="Dispose"/> deletes. No tracked file is touched.
    /// </summary>
    public static string NewContentRootRelative() =>
        global::System.IO.Path.Combine("obj", "standing-instructions-tests", Guid.NewGuid().ToString("N"));

    /// <summary>This directory's own root path, for listing its contents directly (e.g. checking for orphaned temp files).</summary>
    public string Root => _root;

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
