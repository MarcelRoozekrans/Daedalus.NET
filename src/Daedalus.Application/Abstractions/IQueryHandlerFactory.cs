namespace Daedalus.Application.Abstractions;

/// <summary>
///     Factory for creating query handlers from the service provider.
/// </summary>
public interface IQueryHandlerFactory
{
    /// <summary>Gets a query handler for the specified query type.</summary>
    IQueryHandler<TQuery, TResult> GetHandler<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>;
}
