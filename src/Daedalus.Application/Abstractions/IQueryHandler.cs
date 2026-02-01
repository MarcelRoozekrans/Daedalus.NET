namespace Daedalus.Application.Abstractions;

/// <summary>
///     Base interface for query handlers.
/// </summary>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    /// <summary>Executes the query and returns the result.</summary>
    Task<TResult> Handle(TQuery query, CancellationToken cancellationToken);
}
