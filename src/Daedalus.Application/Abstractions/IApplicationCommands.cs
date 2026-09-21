using ZeroAlloc.Results;
using Daedalus.Application.Commands.AbandonTask;
using Daedalus.Application.Commands.CreateProject;
using Daedalus.Application.Commands.CreateTask;
using Daedalus.Application.Commands.DeleteProject;
using Daedalus.Application.Commands.DeleteTask;
using Daedalus.Application.Commands.ResumeTask;
using Daedalus.Application.Commands.UpdateProject;
using Daedalus.Application.Commands.UpdateTask;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     The public dispatch surface for callers outside this assembly. The generated ZeroAlloc.Mediator
///     <c>IMediator</c> is internal to <c>Daedalus.Application</c> and cannot appear in a public signature
///     (CS0051), so the two MVC controllers that dispatch commands depend on this instead. This is a
///     thinner version of the deleted <c>ICommandHandlerFactory</c> - it buys compile-time dispatch and
///     build-time diagnostics for these 8 commands, not less abstraction overall.
/// </summary>
public interface IApplicationCommands
{
    ValueTask<Result<ProjectDto>> CreateProjectAsync(CreateProjectCommand command, CancellationToken cancellationToken);

    ValueTask<Result<ProjectDto>> UpdateProjectAsync(UpdateProjectCommand command, CancellationToken cancellationToken);

    ValueTask<Result> DeleteProjectAsync(DeleteProjectCommand command, CancellationToken cancellationToken);

    ValueTask<Result<TaskDto>> CreateTaskAsync(CreateTaskCommand command, CancellationToken cancellationToken);

    ValueTask<Result<TaskDto>> UpdateTaskAsync(UpdateTaskCommand command, CancellationToken cancellationToken);

    ValueTask<Result> DeleteTaskAsync(DeleteTaskCommand command, CancellationToken cancellationToken);

    ValueTask<Result<TaskDto>> AbandonTaskAsync(AbandonTaskCommand command, CancellationToken cancellationToken);

    ValueTask<Result<TaskDto>> ResumeTaskAsync(ResumeTaskCommand command, CancellationToken cancellationToken);
}
