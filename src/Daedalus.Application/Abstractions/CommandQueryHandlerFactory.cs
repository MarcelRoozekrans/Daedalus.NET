namespace Daedalus.Application.Abstractions;

/// <summary>
///     AOT-compatible factory for resolving command and query handlers from the service provider.
///     This implementation avoids runtime reflection and works with ahead-of-time (AOT) compilation.
/// </summary>
public sealed class CommandQueryHandlerFactory(IServiceProvider serviceProvider)
    : ICommandHandlerFactory, IQueryHandlerFactory
{
    /// <summary>
    ///     Gets a command handler for the specified command type.
    ///     Resolves the handler interface from the service provider without using reflection.
    /// </summary>
    ICommandHandler<TCommand, TResult> ICommandHandlerFactory.GetHandler<TCommand, TResult>(TCommand command)
    {
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(typeof(TCommand), typeof(TResult));
        var handler = serviceProvider.GetService(handlerType);

        return handler as ICommandHandler<TCommand, TResult>
               ?? throw new InvalidOperationException(
                   $"Handler not found for command '{typeof(TCommand).Name}'. " +
                   $"Ensure '{handlerType.Name}' is registered in the DI container.");
    }

    /// <summary>
    ///     Gets a query handler for the specified query type.
    ///     Resolves the handler interface from the service provider without using reflection.
    /// </summary>
    IQueryHandler<TQuery, TResult> IQueryHandlerFactory.GetHandler<TQuery, TResult>(TQuery query)
    {
        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(typeof(TQuery), typeof(TResult));
        var handler = serviceProvider.GetService(handlerType);

        return handler as IQueryHandler<TQuery, TResult>
               ?? throw new InvalidOperationException(
                   $"Handler not found for query '{typeof(TQuery).Name}'. " +
                   $"Ensure '{handlerType.Name}' is registered in the DI container.");
    }
}
