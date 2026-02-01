namespace Daedalus.Domain.Abstractions;

/// <summary>
///     Abstracts system time to enable testability of time-dependent domain logic.
///     Allows tests to control the "current time" instead of relying on system clock.
/// </summary>
public interface ISystemClock
{
    /// <summary>Gets the current UTC time.</summary>
    DateTime UtcNow { get; }
}
