using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Repositories;

/// <summary>
///     The repositories reducers. Pure: no I/O, no clock. An answer is kept only when no newer read has started since its
///     own; see <see cref="RepositoriesState"/>.
/// </summary>
public static class RepositoriesReducers
{
    /// <summary>
    ///     Marks the newest read and clears what the last one showed, as the page always did, so a read that ends with
    ///     the session being renewed shows nothing of its own. A start seen out of order never moves the sequence back.
    /// </summary>
    [Reducer]
    public static RepositoriesState On(RepositoriesState s, RepositoriesLoadStarted a) =>
        s with { Sequence = Math.Max(s.Sequence, a.Sequence), Repositories = null, Forbidden = false, Error = null };

    /// <summary>The list, or the Forbidden state with no list for a refusal.</summary>
    [Reducer]
    public static RepositoriesState On(RepositoriesState s, RepositoriesLoaded a) =>
        a.Sequence < s.Sequence ? s : s with { Repositories = a.Listing.Repositories, Forbidden = a.Listing.Forbidden };

    [Reducer]
    public static RepositoriesState On(RepositoriesState s, RepositoriesLoadFailed a) =>
        a.Sequence < s.Sequence ? s : s with { Error = a.Error };

    /// <summary>Clears the page's fields and keeps the sequence; see <see cref="RepositoriesState"/>.</summary>
    [Reducer]
    public static RepositoriesState On(RepositoriesState s, RepositoriesClosed a) =>
        RepositoriesState.Empty with { Sequence = s.Sequence };
}
