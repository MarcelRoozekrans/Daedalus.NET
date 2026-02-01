using System.Diagnostics.CodeAnalysis;

namespace Daedalus.Tests.Unit.Abstractions;

/// <summary>
///     Base builder class with fluent interface support.
/// </summary>
public abstract class Builder<TBuilder, TEntity> : IBuilder<TEntity>
    where TBuilder : Builder<TBuilder, TEntity>
{
    protected TBuilder Self => (TBuilder)this;
    public abstract TEntity Build();

    public static implicit operator TEntity(Builder<TBuilder, TEntity> builder) => builder.Build();

    /// <summary>
    ///     Converts the builder to the entity type. Named alternative to implicit operator for CA2225 compliance.
    /// </summary>
    public TEntity ToEntity() => Build();

    /// <summary>
    ///     Creates an entity from the builder. Named alternative to implicit operator for CA2225 compliance.
    /// </summary>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
        Justification = "Builder pattern for unit tests")]
    public static TEntity FromBuilder(Builder<TBuilder, TEntity> builder) => builder.Build();
}
