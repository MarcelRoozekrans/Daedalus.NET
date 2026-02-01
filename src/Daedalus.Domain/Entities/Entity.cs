#pragma warning disable S3875 // Allow operator== overload alongside Equals

namespace Daedalus.Domain.Entities;

/// <summary>
///     Base class for all entities in the domain.
///     In DDD, two entities with the same ID are considered equal, regardless of their other properties.
/// </summary>
public abstract class Entity<TId> where TId : notnull
{
    /// <summary>Gets the unique identifier. Set only during creation or by EF Core materialization.</summary>
    public TId Id { get; init; } = default!;

    /// <summary>
    ///     Determines whether two entities are equal based on their ID.
    ///     In DDD, identity is the primary equality metric.
    /// </summary>
    public override bool Equals(object? obj) =>
        obj is Entity<TId> entity && Id.Equals(entity.Id);

    /// <summary>Gets the hash code based on the entity's ID.</summary>
    public override int GetHashCode() => Id.GetHashCode();

    /// <summary>Equality operator for entities.</summary>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.Id.Equals(right.Id);
    }

    /// <summary>Inequality operator for entities.</summary>
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) =>
        !(left == right);
}
