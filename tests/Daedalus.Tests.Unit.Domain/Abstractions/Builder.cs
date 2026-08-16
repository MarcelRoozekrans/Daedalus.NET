namespace Daedalus.Tests.Unit.Domain.Abstractions;

/// <summary>
///     Base builder class with fluent interface support.
/// </summary>
public abstract class Builder<TBuilder, TEntity> : IBuilder<TEntity>
    where TBuilder : Builder<TBuilder, TEntity>
{
    protected TBuilder Self => (TBuilder)this;
    public abstract TEntity Build();

    public static implicit operator TEntity(Builder<TBuilder, TEntity> builder) => builder.Build();
}
