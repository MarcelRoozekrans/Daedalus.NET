using ZeroAlloc.Results;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.AbandonTask;
using Daedalus.Application.Commands.CreateProject;
using Daedalus.Application.Commands.CreateTask;
using Daedalus.Application.Commands.DeleteProject;
using Daedalus.Application.Commands.DeleteTask;
using Daedalus.Application.Commands.ResumeTask;
using Daedalus.Application.Commands.UpdateProject;
using Daedalus.Application.Commands.UpdateTask;
using Daedalus.Application.DTOs;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Services;

/// <summary>
///     Implementation of <see cref="IApplicationCommands"/>. Internal is fine even though it backs a
///     public interface - the DI container resolves it by <see cref="IApplicationCommands"/>, never by
///     its own concrete type, and it is safe to depend on the internal <see cref="IMediator"/> here
///     because both live inside this assembly.
/// </summary>
internal sealed class ApplicationCommands(IMediator mediator) : IApplicationCommands
{
    public ValueTask<Result<ProjectDto>> CreateProjectAsync(CreateProjectCommand command, CancellationToken cancellationToken)
        => mediator.Send(command, cancellationToken);

    public ValueTask<Result<ProjectDto>> UpdateProjectAsync(UpdateProjectCommand command, CancellationToken cancellationToken)
        => mediator.Send(command, cancellationToken);

    public ValueTask<Result> DeleteProjectAsync(DeleteProjectCommand command, CancellationToken cancellationToken)
        => mediator.Send(command, cancellationToken);

    public ValueTask<Result<TaskDto>> CreateTaskAsync(CreateTaskCommand command, CancellationToken cancellationToken)
        => mediator.Send(command, cancellationToken);

    public ValueTask<Result<TaskDto>> UpdateTaskAsync(UpdateTaskCommand command, CancellationToken cancellationToken)
        => mediator.Send(command, cancellationToken);

    public ValueTask<Result> DeleteTaskAsync(DeleteTaskCommand command, CancellationToken cancellationToken)
        => mediator.Send(command, cancellationToken);

    public ValueTask<Result<TaskDto>> AbandonTaskAsync(AbandonTaskCommand command, CancellationToken cancellationToken)
        => mediator.Send(command, cancellationToken);

    public ValueTask<Result<TaskDto>> ResumeTaskAsync(ResumeTaskCommand command, CancellationToken cancellationToken)
        => mediator.Send(command, cancellationToken);
}
