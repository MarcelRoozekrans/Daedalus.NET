#pragma warning disable CA1040 // Avoid empty interfaces for CQRS pattern marker types

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Base interface for all commands (write operations) with a result type.
/// </summary>
#pragma warning disable S2326 // Unused generic type parameter is intentional for use case queries
public interface ICommand<out TResult> : ICommand
{
}
#pragma warning restore S2326
/// <summary>
///     Base interface for all queries (read operations) with a result type.
/// </summary>
#pragma warning disable S2326 // Unused generic type parameter is intentional for use case queries
public interface IQuery<out TResult> : IQuery
{
}
#pragma warning restore S2326

#pragma warning restore CA1040
