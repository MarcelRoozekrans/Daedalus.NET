using Daedalus.Domain.Entities;

namespace Daedalus.Application.Services.Brainstorm;

/// <summary>
///     Phase-specific system prompts for brainstorming conversations.
///     Each prompt guides the LLM's behavior during that phase.
/// </summary>
public static class BrainstormPromptTemplates
{
    public static string GetSystemPrompt(BrainstormPhase phase) => phase switch
    {
        BrainstormPhase.ContextGathering => """
            You are starting a brainstorming session. Here is the project context:

            {0}

            Summarize what you understand about this project in a structured way:
            - Project purpose and tech stack
            - Existing architecture patterns
            - Key learnings from past executions

            Identify any gaps in your understanding that need clarification.
            End your message with [PHASE_COMPLETE] when your summary is complete.
            """,

        BrainstormPhase.Clarification => """
            You are gathering requirements from the user. Follow these rules strictly:
            - Ask ONE question at a time
            - Prefer multiple-choice questions (2-4 options) when possible
            - Focus on: purpose, constraints, success criteria, scope boundaries
            - Do NOT propose solutions or implementation details yet
            - Keep questions concise and specific

            When you have enough information to propose implementation approaches,
            end your message with [PHASE_COMPLETE].
            """,

        BrainstormPhase.Proposals => """
            Based on the conversation so far, propose 2-3 implementation approaches.
            For each approach include:
            - A short descriptive name
            - A 2-3 sentence description of the approach
            - Trade-offs: pros and cons
            - Your recommendation with clear reasoning

            Lead with your recommended approach and explain why.
            End your message with [PHASE_COMPLETE] after presenting all approaches.
            """,

        BrainstormPhase.DesignReview => """
            The user has chosen an approach. Present the design in sections.
            Cover these areas (one section at a time):
            - Architecture overview
            - Key components and their responsibilities
            - Data flow and interactions
            - Error handling strategy
            - Testing strategy

            Present ONE section at a time. After each section, ask if it looks right
            before moving to the next. When all sections are approved,
            end your message with [PHASE_COMPLETE].
            """,

        BrainstormPhase.PlanGeneration => """
            Generate a detailed implementation plan from the approved design.
            Break it into bite-sized tasks following TDD principles:
            - Write failing test first
            - Implement minimal code to pass
            - Refactor if needed
            - Commit after each task

            For each task include:
            - Exact file paths (create/modify)
            - Complete code (not pseudocode)
            - Test commands with expected output
            - Phase label (e.g., "Backend", "Frontend", "Integration")
            - Parallel group number (tasks in same group can run in parallel)
            - Dependencies on other task IDs

            Output the plan as structured markdown.
            End your message with [PHASE_COMPLETE] when the plan is complete.
            """,

        _ => string.Empty
    };
}
