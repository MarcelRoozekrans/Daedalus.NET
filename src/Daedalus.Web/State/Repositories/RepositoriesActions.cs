namespace Daedalus.Web.State.Repositories;

/// <summary>A read of the repositories was started, numbered <paramref name="Sequence"/>.</summary>
public readonly record struct RepositoriesLoadStarted(long Sequence);

/// <summary>Read <paramref name="Sequence"/> answered with <paramref name="Listing"/>: the list, or a refusal.</summary>
public readonly record struct RepositoriesLoaded(long Sequence, RepositoryListing Listing);

/// <summary>Read <paramref name="Sequence"/> failed with <paramref name="Error"/>.</summary>
public readonly record struct RepositoriesLoadFailed(long Sequence, string Error);

/// <summary>The Git Repositories page closed: its list goes.</summary>
public readonly record struct RepositoriesClosed;
