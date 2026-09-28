using System.Diagnostics;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Creates a directory link: a junction on Windows, which needs no privilege, and a symlink elsewhere, which needs
///     none either. A failure throws, so a test that needs a link can never pass without one. Copied in spirit from
///     Thalos's <c>WorkspacePathTests</c>, which checks the same kind of link against <c>WorkspacePath.Resolve</c>.
///     Disposing removes the link itself, never its target, before the directory holding it is deleted: a recursive
///     delete of a directory that holds a junction whose target is already gone is refused on Windows.
/// </summary>
internal sealed class DirectoryLink : IDisposable
{
    private readonly string _linkPath;

    private DirectoryLink(string linkPath) => _linkPath = linkPath;

    public static DirectoryLink Create(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return new DirectoryLink(linkPath);
        }

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start cmd.exe.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(linkPath))
        {
            throw new InvalidOperationException($"mklink /J failed ({process.ExitCode}): {error}");
        }

        return new DirectoryLink(linkPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_linkPath) || File.Exists(_linkPath))
        {
            Directory.Delete(_linkPath);
        }
    }
}
