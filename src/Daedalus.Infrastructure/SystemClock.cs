using Daedalus.Domain.Abstractions;

namespace Daedalus.Infrastructure;

/// <summary>
///     Provides access to the system clock (current UTC time).
/// </summary>
public sealed class SystemClock : ISystemClock
{
    /// <summary>Gets the current UTC time from the system clock.</summary>
    public DateTime UtcNow => DateTime.UtcNow;
}
