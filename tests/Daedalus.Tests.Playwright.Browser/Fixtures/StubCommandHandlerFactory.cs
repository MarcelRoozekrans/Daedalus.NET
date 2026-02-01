using Daedalus.Application.Abstractions;

namespace Daedalus.Tests.Playwright.Browser;

internal sealed class StubCommandHandlerFactory : ICommandHandlerFactory
{
    public ICommandHandler<TCommand, TResult> GetHandler<TCommand, TResult>(TCommand command)
        where TCommand : ICommand<TResult> =>
        throw new NotSupportedException(
            $"Command handler for {typeof(TCommand).Name} is not available in the E2E test environment.");
}
