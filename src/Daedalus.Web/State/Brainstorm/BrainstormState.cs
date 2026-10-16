using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Brainstorm;

/// <summary>
///     The Brainstorm page: the session it shows, with its phase and messages. Whether a send, an advance or a task
///     generation is in flight is the page's own: only the page asks, and it shows its spinner for its own request. Their
///     outcomes are not state either: each effect returns its outcome, and the page shows a refusal as a toast.
///     <para>
///         Lifetime: page-only, cleared by <see cref="BrainstormClosed"/> when the page is disposed. Only that page reads
///         or loads it, so its clear races no other reader. <paramref name="Sequence"/> survives the clear, so an answer
///         to a read made before it never lands in a read made after it.
///     </para>
/// </summary>
/// <param name="Session">
///     The session, or <see langword="null"/> before its first read landed. A failed read keeps the session the page
///     held, as the page always did.
/// </param>
/// <param name="Sequence">
///     The number of the newest read or advance started, never reset; an answer to any other is dropped.
/// </param>
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct BrainstormState(BrainstormSessionDto? Session, long Sequence)
{
    /// <summary>The page with nothing read, numbered from the start.</summary>
    public static BrainstormState Empty { get; } = new(null, 0);

    /// <summary>The state before any read.</summary>
    public static BrainstormState Initial(IServiceProvider services) => Empty;
}
