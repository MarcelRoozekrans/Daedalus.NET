using Daedalus.Application.DTOs.WorkflowRuns;

namespace Daedalus.Web.State.Home;

/// <summary>Home opened for someone who may view runs: its cards load, and a run change or a reconnect re-fetches them.</summary>
public readonly record struct HomeOpened;

/// <summary>
///     A read of <paramref name="Card"/> was started, numbered <paramref name="Sequence"/>. A
///     <paramref name="Background"/> read, a run change's or a reconnect's, keeps the rows on show; any other read shows
///     the card's skeleton until it lands.
/// </summary>
public readonly record struct HomeCardLoadStarted(HomeCardKind Card, long Sequence, bool Background);

/// <summary>
///     Read <paramref name="Sequence"/> of <paramref name="Card"/> arrived at <paramref name="ReadAt"/> with
///     <paramref name="Rows"/>; <paramref name="More"/> says the list holds more than the page read.
/// </summary>
public readonly record struct HomeCardLoaded(
    HomeCardKind Card, long Sequence, IReadOnlyList<WorkflowRunListItem> Rows, bool More, DateTimeOffset ReadAt);

/// <summary>Read <paramref name="Sequence"/> of <paramref name="Card"/> failed with <paramref name="Error"/>.</summary>
public readonly record struct HomeCardLoadFailed(HomeCardKind Card, long Sequence, string Error);

/// <summary>
///     Home was left, or is shown to someone who may not view runs: the cards go, and a run change or a reconnect no
///     longer re-fetches what no page shows.
/// </summary>
public readonly record struct HomeClosed;
