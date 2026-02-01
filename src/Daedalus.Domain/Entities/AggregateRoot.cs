namespace Daedalus.Domain.Entities;

/// <summary>
///     Base class for aggregate roots in domain-driven design.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
{
}
