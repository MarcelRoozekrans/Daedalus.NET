using Daedalus.Agents.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Pins the one property <c>Daedalus.Api.csproj</c>'s <c>processes\**\*.yaml</c> copy rule depends on:
///     what the build deploys is what this source reads.
/// </summary>
/// <remarks>
///     The copy rule preserves <c>%(RecursiveDir)</c>, so <c>processes/sub/thing.yaml</c> is deployed at
///     <c>processes/sub/thing.yaml</c>. While this source enumerated <see cref="SearchOption.TopDirectoryOnly"/>
///     that file deployed correctly and never loaded, with nothing logged and nothing failing. Reverting
///     <see cref="FileSystemProcessDefinitionSource.ReadAllAsync"/> to <see cref="SearchOption.TopDirectoryOnly"/>
///     turns <see cref="A_yaml_file_in_a_subdirectory_is_read"/> red — verified by making exactly that edit.
/// </remarks>
public sealed class FileSystemProcessDefinitionSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"daedalus-processes-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_yaml_file_in_a_subdirectory_is_read()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_root, "top.yaml"), "process: top");
        await File.WriteAllTextAsync(Path.Combine(_root, "sub", "nested.yaml"), "process: nested");

        var documents = await new FileSystemProcessDefinitionSource(_root).ReadAllAsync(CancellationToken.None);

        documents.Select(d => d.Yaml).Should().BeEquivalentTo(["process: top", "process: nested"],
            "Daedalus.Api.csproj copies processes\\**\\*.yaml with %(RecursiveDir) preserved, so a nested file "
            + "is deployed nested and must load rather than be silently skipped");
    }

    /// <summary>
    ///     The documented tolerance for a host that has no <c>processes</c> folder in its deployment layout —
    ///     boot and sync zero definitions rather than throw. Every Integration host that disables the workflow
    ///     engine relies on this staying true.
    /// </summary>
    [Fact]
    public async Task A_missing_root_yields_no_documents()
    {
        var documents = await new FileSystemProcessDefinitionSource(_root).ReadAllAsync(CancellationToken.None);

        documents.Should().BeEmpty();
    }
}
