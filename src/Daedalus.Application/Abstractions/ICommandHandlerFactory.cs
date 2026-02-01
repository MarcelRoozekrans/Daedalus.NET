namespace Daedalus.Application.Abstractions;

/// <summary>
///     Factory for creating command handlers from the service provider.
/// </summary>
public interface ICommandHandlerFactory
{
    /// <summary>Gets a command handler for the specified command type.</summary>
    ICommandHandler<TCommand, TResult> GetHandler<TCommand, TResult>(TCommand command)
        where TCommand : ICommand<TResult>;
}
