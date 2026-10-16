namespace Daedalus.Web.State.Brainstorm;

/// <summary>A read of the session, or an advance, was started, numbered <paramref name="Sequence"/>.</summary>
public readonly record struct BrainstormReadStarted(long Sequence);

/// <summary>Read or advance <paramref name="Sequence"/> answered with <paramref name="Session"/>.</summary>
public readonly record struct BrainstormLoaded(long Sequence, BrainstormSessionDto Session);

/// <summary>The Brainstorm page closed: the session it showed goes.</summary>
public readonly record struct BrainstormClosed;
