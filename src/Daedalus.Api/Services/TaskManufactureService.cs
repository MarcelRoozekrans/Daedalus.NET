using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Infrastructure.Services.GitHub;
using Thalos.Workflow;
using ZeroAlloc.Results;
using DomainTask = Daedalus.Domain.Entities.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Api.Services;

/// <summary>Why a task's manufacture start was refused or failed. <c>TasksController.Manufacture</c> maps each kind to a status.</summary>
public enum TaskManufactureFailureKind
{
    /// <summary>The task does not exist. 404.</summary>
    TaskNotFound,

    /// <summary>The project's repository matches no allow-listed entry. 422.</summary>
    RepositoryNotAllowed,

    /// <summary>A dependency's derived status is not Completed, or the dependency does not exist. 422.</summary>
    DependencyNotCompleted,

    /// <summary>The task's current run is Running or Awaiting. 409.</summary>
    RunLive,

    /// <summary>The starter refused or failed; <see cref="TaskManufactureFailure.Start"/> says how. Mapped as <c>POST /api/workflow-runs</c> maps it.</summary>
    StartFailed,

    /// <summary>
    ///     The run started, but the task was changed by another request after it was read, so the attach lost the
    ///     row-version race and was not saved. 409, naming the run so a person can cancel it. No second run is started.
    /// </summary>
    AttachConflict,

    /// <summary>The run started, but could not be attached to the task. 500, naming the run so a person can cancel it.</summary>
    AttachFailed,

    /// <summary>A read failed. 500.</summary>
    Failed,
}

/// <summary>A refused or failed start.</summary>
/// <param name="Kind">Why.</param>
/// <param name="Message">Text that is safe to show the caller.</param>
/// <param name="Start">The starter's own failure, for <see cref="TaskManufactureFailureKind.StartFailed"/>.</param>
/// <param name="RunId">The run that started but was not attached, for the two attach kinds.</param>
public sealed record TaskManufactureFailure(TaskManufactureFailureKind Kind, string Message, ManufactureStartFailure? Start = null, Guid? RunId = null);

/// <summary>
///     Starts a manufacture run for a board task (phase 2.8, D1 and D2). Every refusal comes before the starter is called,
///     so a refused start spends nothing. See the spec's §2 for the order of the checks.
/// </summary>
public sealed class TaskManufactureService(
    ITaskRepository tasks,
    IProjectRepository projects,
    IWorkflowRunStatusReader runs,
    IManufactureRunStarter starter,
    WorkflowConfig workflow)
{
    /// <summary>
    ///     Starts the run and attaches it. Returns the run id. The attach is saved on the instance that was read, so its
    ///     row version guards it: when the task changed in between, the save is refused and
    ///     <see cref="TaskManufactureFailureKind.AttachConflict"/> is returned for the run that already started.
    /// </summary>
    public async Task<Result<Guid, TaskManufactureFailure>> StartAsync(Guid taskId, RunPrincipal startedBy, CancellationToken ct)
    {
        var found = await tasks.GetByIdAsync(taskId, ct).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return Fail(
                found.Error.Contains("not found", StringComparison.OrdinalIgnoreCase) ? TaskManufactureFailureKind.TaskNotFound : TaskManufactureFailureKind.Failed,
                found.Error);
        }

        var task = found.Value;
        var project = await projects.GetByIdAsync(task.ProjectId, ct).ConfigureAwait(false);
        if (project.IsFailure)
        {
            return Fail(TaskManufactureFailureKind.Failed, project.Error);
        }

        var repository = MatchRepository(project.Value.RepositoryUrl, workflow.Repositories);
        if (repository is null)
        {
            return Fail(
                TaskManufactureFailureKind.RepositoryNotAllowed,
                $"The project's repository '{project.Value.RepositoryUrl}' matches no allow-listed entry in {WorkflowConfig.SectionName}:Repositories.");
        }

        var dependency = await FindBlockingDependencyAsync(task, ct).ConfigureAwait(false);
        if (dependency is not null)
        {
            return Result<Guid, TaskManufactureFailure>.Failure(dependency);
        }

        var current = await runs.ReadRunAsync(task, ct).ConfigureAwait(false);
        if (current.IsLive)
        {
            return Fail(TaskManufactureFailureKind.RunLive, TaskRunGuard.LiveRun(task, current));
        }

        var started = await starter.StartAsync(new ManufactureStartRequest(WorkIntent(task), repository.Name, startedBy), ct).ConfigureAwait(false);
        if (started.IsFailure)
        {
            return Result<Guid, TaskManufactureFailure>.Failure(
                new TaskManufactureFailure(TaskManufactureFailureKind.StartFailed, started.Error.Message, started.Error));
        }

        var runId = started.Value;
        var attached = task.AttachRun(runId);
        var saved = attached.IsSuccess ? await tasks.UpdateAsync(task, ct).ConfigureAwait(false) : attached;
        if (saved.IsSuccess)
        {
            return Result<Guid, TaskManufactureFailure>.Success(runId);
        }

        var kind = saved.Error.StartsWith(TaskRunGuard.ChangedPrefix, StringComparison.Ordinal)
            ? TaskManufactureFailureKind.AttachConflict
            : TaskManufactureFailureKind.AttachFailed;
        return Result<Guid, TaskManufactureFailure>.Failure(new TaskManufactureFailure(
            kind,
            $"Run {runId} started, but it could not be attached to task {task.TaskId}: {saved.Error} Cancel run {runId} if it is not wanted.",
            RunId: runId));
    }

    /// <summary>The work intent: the task's title, a blank line, then its description.</summary>
    internal static string WorkIntent(DomainTask task) => $"{task.Title}\n\n{task.Description}";

    /// <summary>
    ///     The allow-listed entry for <paramref name="projectUrl"/>, or null (ruling P2). Two github.com URLs match on
    ///     owner and name, ignoring case and form. An entry whose remote is not a github.com URL matches only the same
    ///     remote string, after one trailing <c>/</c> and one trailing <c>.git</c> are trimmed from each, compared
    ///     ordinally. No entry is ever thrown on.
    /// </summary>
    internal static RepositoryConfig? MatchRepository(string? projectUrl, IEnumerable<RepositoryConfig> repositories)
    {
        if (string.IsNullOrWhiteSpace(projectUrl))
        {
            return null;
        }

        var project = RepoRef.FromGitHubUrl(projectUrl);
        var remote = NormalizeRemote(projectUrl);
        foreach (var repository in repositories)
        {
            var configured = RepoRef.FromGitHubUrl(repository.Remote);
            if (configured.IsSuccess)
            {
                if (project.IsSuccess && string.Equals(project.Value.ToString(), configured.Value.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    return repository;
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(repository.Remote) && string.Equals(NormalizeRemote(repository.Remote), remote, StringComparison.Ordinal))
            {
                return repository;
            }
        }

        return null;
    }

    private static string NormalizeRemote(string remote)
    {
        var trimmed = remote.EndsWith('/') ? remote[..^1] : remote;
        return trimmed.EndsWith(".git", StringComparison.Ordinal) ? trimmed[..^4] : trimmed;
    }

    private async Task<TaskManufactureFailure?> FindBlockingDependencyAsync(DomainTask task, CancellationToken ct)
    {
        if (task.Dependencies.Count == 0)
        {
            return null;
        }

        var siblings = await tasks.GetByProjectIdAsync(task.ProjectId, ct).ConfigureAwait(false);
        if (siblings.IsFailure)
        {
            return new TaskManufactureFailure(TaskManufactureFailureKind.Failed, siblings.Error);
        }

        foreach (var name in task.Dependencies)
        {
            var dependency = siblings.Value.FirstOrDefault(t => string.Equals(t.TaskId, name, StringComparison.Ordinal));
            if (dependency is null)
            {
                return new TaskManufactureFailure(
                    TaskManufactureFailureKind.DependencyNotCompleted, $"Dependency {name} of task {task.TaskId} does not exist in its project.");
            }

            var status = TaskStatusDerivation.Derive(dependency, await runs.ReadRunAsync(dependency, ct).ConfigureAwait(false));
            if (status != TaskStatus.Completed)
            {
                return new TaskManufactureFailure(TaskManufactureFailureKind.DependencyNotCompleted, $"Dependency {name} is {status}, not Completed.");
            }
        }

        return null;
    }

    private static Result<Guid, TaskManufactureFailure> Fail(TaskManufactureFailureKind kind, string message) =>
        Result<Guid, TaskManufactureFailure>.Failure(new TaskManufactureFailure(kind, message));
}
