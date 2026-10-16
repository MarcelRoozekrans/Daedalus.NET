namespace Daedalus.Web.State.Repositories;

/// <summary>A read of the repositories was started, numbered <paramref name="Sequence"/>.</summary>
public readonly record struct RepositoriesLoadStarted(long Sequence);

/// <summary>Read <paramref name="Sequence"/> answered with <paramref name="Listing"/>: the list, or a refusal.</summary>
public readonly record struct RepositoriesLoaded(long Sequence, RepositoryListing Listing);

/// <summary>Read <paramref name="Sequence"/> failed with <paramref name="Error"/>.</summary>
public readonly record struct RepositoriesLoadFailed(long Sequence, string Error);

/// <summary>A <paramref name="Write"/> was started.</summary>
public readonly record struct RepositorySaveStarted(RepositoryWrite Write);

/// <summary>The <paramref name="Write"/> was saved, or the connection test passed.</summary>
public readonly record struct RepositorySaved(RepositoryWrite Write);

/// <summary>The <paramref name="Write"/> was refused or got no answer, as <paramref name="Reason"/> says.</summary>
public readonly record struct RepositorySaveRefused(RepositoryWrite Write, string Reason);

/// <summary>The Git Repositories page closed: its list and last outcome go.</summary>
public readonly record struct RepositoriesClosed;
