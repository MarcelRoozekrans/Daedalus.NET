using Microsoft.AspNetCore.SignalR.Client;

namespace Daedalus.Web.State;

/// <summary>
///     When the run hub tries again, after a dropped connection or a start that failed: at once, then after 2, 5 and
///     10 seconds, then every 30 seconds for as long as it takes. It never gives up, because the console stays open
///     for hours and an API restart or a network blip outlasts SignalR's default schedule, which stops after about 42
///     seconds and leaves the page offline until a reload.
/// </summary>
public sealed class HubRetryPolicy : IRetryPolicy
{
    /// <summary>The longest wait between two tries, reached from the fifth try on.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan[] FirstDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <inheritdoc />
    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        retryContext.PreviousRetryCount < FirstDelays.Length ? FirstDelays[retryContext.PreviousRetryCount] : Cap;
}
