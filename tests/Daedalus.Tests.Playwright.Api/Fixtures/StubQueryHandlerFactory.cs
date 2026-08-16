using Daedalus.Application.Abstractions;

namespace Daedalus.Tests.Playwright.Api;

internal sealed class StubQueryHandlerFactory : IQueryHandlerFactory
{
    public IQueryHandler<TQuery, TResult> GetHandler<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        throw new NotSupportedException(
            $"Query handler for {typeof(TQuery).Name} is not available in the E2E test environment.");
    }
}
