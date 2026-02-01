using CSharpFunctionalExtensions;
using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Application.Services.CodeAnalysis;

/// <summary>
///     Applies code changes from AI responses
/// </summary>
public interface IGitChangeApplier
{
    // Extract changes from AI response
    Task<Result<IReadOnlyList<CodeModification>>> ExtractChangesAsync(
        string aiResponse,
        CancellationToken ct = default);

    // Apply changes to working directory
    Task<Result> ApplyChangesAsync(
        string workTreePath,
        IReadOnlyList<CodeModification> changes,
        CancellationToken ct = default);

    // Generate patch
    Task<Result<string>> GeneratePatchAsync(
        string workTreePath,
        string baseBranch,
        CancellationToken ct = default);

    // Revert changes
    Task<Result> RevertChangesAsync(
        string workTreePath,
        string baseBranch,
        CancellationToken ct = default);
}
