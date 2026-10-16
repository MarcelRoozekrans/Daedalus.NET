using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Prd;

/// <summary>The PRD generator's steps, in the order the wizard walks them; the value is the step indicator's index.</summary>
public enum PrdStep
{
    /// <summary>Pick the project the PRD is for.</summary>
    ProjectSelection = 0,

    /// <summary>Enter the requirements and generate.</summary>
    RequirementsInput = 1,

    /// <summary>Review the generated PRD and pick the items to convert.</summary>
    PrdReview = 2,

    /// <summary>The tasks the conversion created.</summary>
    TasksCreated = 3,
}

/// <summary>
///     The PRD generator: the step the wizard is on, the project it is for, the generated PRD, the tasks the conversion
///     created and the last request's failure. Whether a generate or a convert is in flight is the page's own: only the
///     page asks, and it shows its spinner for its own request.
///     <para>
///         Lifetime: page-only, cleared by <see cref="PrdClosed"/> when the page is disposed and by <see cref="PrdReset"/>
///         when the wizard starts over. Only that page reads or writes it, so a clear races no other reader.
///         <paramref name="Sequence"/> survives both, so an answer to a request made before them never lands after them.
///     </para>
/// </summary>
/// <param name="Step">The step the wizard shows.</param>
/// <param name="ProjectId">The project the PRD is for; <see cref="Guid.Empty"/> before one is picked.</param>
/// <param name="Prd">The generated PRD, or <see langword="null"/> before one was generated.</param>
/// <param name="CreatedTasks">The tasks the conversion created; empty before it ran.</param>
/// <param name="Error">
///     Why the last generate or convert failed, or <see langword="null"/>. Never <see cref="Auth.SessionExpiry.SigningInAgain"/>,
///     and never a cancellation: neither is stored.
/// </param>
/// <param name="Sequence">The number of the newest request started, never reset; an answer to any other is dropped.</param>
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct PrdState(
    PrdStep Step,
    Guid ProjectId,
    PrdResponseDto? Prd,
    IReadOnlyList<TaskDto> CreatedTasks,
    string? Error,
    long Sequence)
{
    /// <summary>The wizard on its first step, with nothing asked.</summary>
    public static PrdState Empty { get; } = new(PrdStep.ProjectSelection, Guid.Empty, null, [], null, 0);

    /// <summary>The state before any request.</summary>
    public static PrdState Initial(IServiceProvider services) => Empty;
}
