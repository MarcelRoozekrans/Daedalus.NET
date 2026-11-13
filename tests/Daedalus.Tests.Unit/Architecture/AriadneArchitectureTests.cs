using Ariadne.NET.Testing;

namespace Daedalus.Tests.Unit.Architecture;

/// <summary>
///     Checks the solution against the Ariadne pack in <c>.architecture/rules.yaml</c>: the layers, the Thalos, ORM,
///     outbox, Cronos and Rag.NET boundaries, the single <c>ISubagentRunner</c> seam, where endpoints and repositories
///     live, the Web's DTO copies and the banned packages. Each rule's <c>because</c> says why it exists.
/// </summary>
public sealed class AriadneArchitectureTests
{
    private const string SolutionFileName = "Daedalus.sln";

    /// <summary>
    ///     The whole solution follows the pack. Ariadne loads <c>Daedalus.sln</c> with MSBuildWorkspace and reads the
    ///     restored package graphs, so the solution must be restored. Red: add a field of type
    ///     <c>Daedalus.Application.DTOs.ProjectDto</c> to a Domain entity; the failure names <c>DAE-LAYER-DOMAIN</c>.
    ///     Red: move <c>.architecture/rules.yaml</c> aside; the run fails because the folder holds no pack.
    /// </summary>
    [Fact]
    public void Solution_follows_the_architecture_pack() =>
        ArchitectureCheck.AssertValid(FindSolution());

    private static string FindSolution()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, SolutionFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"No {SolutionFileName} found in {AppContext.BaseDirectory} or any folder above it.");
    }
}
