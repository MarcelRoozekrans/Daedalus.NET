namespace Daedalus.Domain.Entities;

/// <summary>
///     Represents the phase of a brainstorming session. The first six values are interactive phases
///     that progress sequentially, while the final two are terminal states.
/// </summary>
public enum BrainstormPhase
{
    /// <summary>Gathering repository context, codebase structure, and relevant files.</summary>
    ContextGathering = 0,

    /// <summary>Asking clarifying questions to refine the user's intent.</summary>
    Clarification = 1,

    /// <summary>Generating and presenting design proposals for discussion.</summary>
    Proposals = 2,

    /// <summary>Reviewing and refining the chosen design approach.</summary>
    DesignReview = 3,

    /// <summary>Generating a detailed implementation plan from the approved design.</summary>
    PlanGeneration = 4,

    /// <summary>Creating actionable tasks from the implementation plan.</summary>
    TaskCreation = 5,

    /// <summary>Terminal state indicating the brainstorming session finished successfully.</summary>
    Completed = 6,

    /// <summary>Terminal state indicating the brainstorming session was abandoned.</summary>
    Abandoned = 7
}
