using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Repositories;

/// <summary>
///     The Git Repositories page's list and the outcome of its last write.
///     <para>
///         Lifetime: page-only, cleared by <see cref="RepositoriesClosed"/> when the page is disposed. Only that page
///         reads or loads it, so its clear races no other reader. <paramref name="Sequence"/> survives the clear, so an
///         answer to a read made before it never lands in a read made after it.
///     </para>
/// </summary>
/// <param name="Repositories">
///     The repositories, or <see langword="null"/> before a read has answered, after a refusal or a failure, and after a
///     read that ended with the session being renewed: the layout says so, and the page shows nothing of its own.
/// </param>
/// <param name="Forbidden">Whether the server refused the read, a 403: the user is not an admin.</param>
/// <param name="Error">
///     Why the newest read failed, or <see langword="null"/>. Never <see cref="Auth.SessionExpiry.SigningInAgain"/>, and
///     never a cancellation: neither is stored.
/// </param>
/// <param name="Sequence">The number of the newest read started, never reset; an answer to an older one is dropped.</param>
/// <param name="LastSave">
///     The outcome of the last create, update, delete or connection test, or <see langword="null"/> while one is in
///     flight, before any, and after one that ended with the session being renewed or its caller gone. A write is not
///     cancelled by the page closing, so its outcome can land after <see cref="RepositoriesClosed"/>. It is read only by
///     the component that awaited that write, at once and by its <see cref="RepositorySave.Write"/>.
/// </param>
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct RepositoriesState(
    IReadOnlyList<RepositoryConfigurationDto>? Repositories, bool Forbidden, string? Error, long Sequence,
    RepositorySave? LastSave)
{
    /// <summary>The state before any read.</summary>
    public static RepositoriesState Initial(IServiceProvider services) => new(null, false, null, 0, null);
}
