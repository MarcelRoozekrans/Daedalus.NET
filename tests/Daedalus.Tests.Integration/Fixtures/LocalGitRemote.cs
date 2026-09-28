using System.Diagnostics;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     A real bare git repository in a temp directory whose <c>main</c> holds <c>README.md</c> plus the files a test
///     seeds, for a booted host's <c>GitWorktreeWorkspaceProvider</c> to clone and fetch from as a genuine git remote:
///     no fake transport and no mocked git. Copied from Thalos's <c>Thalos.NET.Tests.Git</c> helper of the same name.
///     Every commit is made with <c>-c user.name=t -c user.email=t@example.invalid</c>, because CI has no git identity.
/// </summary>
internal sealed class LocalGitRemote : IDisposable
{
    private readonly string _path;

    private LocalGitRemote(string path) => _path = path;

    /// <summary>The bare repository's path, usable directly as a git remote URL, since a local path is a valid one.</summary>
    public string Url => _path;

    /// <summary>
    ///     Creates a bare repository whose <c>main</c> holds <c>README.md</c> and <paramref name="files"/>. An entry
    ///     named <c>README.md</c> replaces the default content; a path with <c>/</c> creates its folders.
    /// </summary>
    public static LocalGitRemote Create(params (string Path, string Content)[] files)
    {
        var seeds = new Dictionary<string, string>(StringComparer.Ordinal) { ["README.md"] = "# sandbox\n" };
        foreach (var (path, content) in files)
        {
            seeds[path] = content;
        }

        var bare = Directory.CreateTempSubdirectory("daedalus-git-remote-bare-").FullName;
        RunGit(bare, "init", "--bare", "--initial-branch=main");

        var seed = Directory.CreateTempSubdirectory("daedalus-git-remote-seed-").FullName;
        try
        {
            RunGit(seed, "init", "--initial-branch=main");
            foreach (var (path, content) in seeds)
            {
                var full = Path.Combine(seed, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }

            RunGit(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "add", "-A");
            RunGit(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "seed");
            RunGit(seed, "remote", "add", "origin", bare);
            RunGit(seed, "push", "origin", "main");
        }
        finally
        {
            DeleteReadOnly(seed);
        }

        return new LocalGitRemote(bare);
    }

    /// <summary>The commit sha at the tip of <paramref name="branch"/> in the bare repository.</summary>
    public string HeadOf(string branch) => RunGit(_path, "rev-parse", branch);

    /// <summary>
    ///     The newest <paramref name="count"/> commits on <paramref name="branch"/> in the bare repository, newest first,
    ///     each with the files it changed.
    /// </summary>
    public IReadOnlyList<RemoteCommit> Log(string branch, int count)
    {
        var shas = RunGit(_path, "rev-list", $"--max-count={count}", branch)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return
        [
            .. shas.Select(sha => new RemoteCommit(
                sha,
                RunGit(_path, "log", "-1", "--format=%B", sha),
                RunGit(_path, "show", "--name-only", "--format=", sha)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))),
        ];
    }

    /// <summary>The content of <paramref name="path"/> at the tip of <paramref name="branch"/>: <c>git show branch:path</c>.</summary>
    public string Show(string branch, string path) => RunGit(_path, "show", $"{branch}:{path}");

    /// <summary>Deletes the bare repository's directory, so a push to it fails.</summary>
    public void Delete() => DeleteReadOnly(_path);

    /// <summary>One commit in the bare repository.</summary>
    /// <param name="Sha">The commit's sha.</param>
    /// <param name="Message">The full commit message, trimmed.</param>
    /// <param name="Files">The files the commit changed, from <c>git show --name-only --format=</c>.</param>
    public sealed record RemoteCommit(string Sha, string Message, IReadOnlyList<string> Files);

    /// <summary>
    ///     Runs git in <paramref name="workingDirectory"/> with <paramref name="arguments"/>, split on spaces, and
    ///     returns its trimmed standard output. Throws on a non-zero exit, so a test that expected output fails loudly.
    /// </summary>
    public static string Git(string workingDirectory, string arguments) =>
        RunGit(workingDirectory, arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <inheritdoc />
    public void Dispose() => DeleteReadOnly(_path);

    /// <summary>
    ///     Deletes a directory git created, clearing the read-only attribute git sets on files under
    ///     <c>.git/objects</c> first; otherwise a plain recursive delete throws <see cref="UnauthorizedAccessException"/>
    ///     on Windows.
    /// </summary>
    internal static void DeleteReadOnly(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stdErr}");
        }

        return stdOut.Trim();
    }
}
