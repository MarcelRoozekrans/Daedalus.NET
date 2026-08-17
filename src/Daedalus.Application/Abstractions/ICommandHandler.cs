namespace Daedalus.Application.Abstractions;

/// <summary>
///     Base interface for command handlers.
/// </summary>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    /// <summary>Executes the command and returns the result.</summary>
    Task<TResult> Handle(TCommand command, CancellationToken cancellationToken);
}
