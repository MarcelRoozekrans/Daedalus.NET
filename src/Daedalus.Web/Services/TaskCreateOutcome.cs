namespace Daedalus.Web.Services;

/// <summary>
///     What the create dialog closed with: the new task, and for Create and manufacture either the run it started or why
///     the start was refused. The task exists in every case.
/// </summary>
/// <param name="TaskId">The created task.</param>
/// <param name="RunId">The run Create and manufacture started, or null.</param>
/// <param name="ManufactureRefusal">Why Create and manufacture could not start the run, or null.</param>
public sealed record TaskCreateOutcome(Guid TaskId, Guid? RunId, string? ManufactureRefusal);
