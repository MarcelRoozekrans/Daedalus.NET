# Ralph Wiggum AI Loop Technique

## Overview

The Ralph Wiggum technique is an iterative AI development methodology that uses persistent looping to feed an AI agent the same prompt repeatedly until a completion signal is received. Named after The Simpsons character, it embodies the philosophy of persistent iteration despite setbacks.

## Core Philosophy

- **Iteration > Perfection**: Don't aim for perfect on first try; let the loop refine the work
- **Failures Are Data**: Deterministically bad failures are predictable and informative
- **Operator Skill Matters**: Success depends on writing good prompts, not just having a good model
- **Persistence Wins**: Keep trying until success; the loop handles retry logic automatically

## How It Works

At its core, Ralph is a Bash loop that repeatedly processes the same prompt:

```bash
while :; do cat PROMPT.md | claude ; done
```

The loop continues until a completion promise (exact string match) is received or max iterations is reached.

## Key Principles for Success

### 1. Clear Completion Criteria

Define explicit success conditions and output markers. Instead of "Build a todo API and make it good," specify all CRUD endpoints, validation, test coverage requirements, and the exact completion phrase.

### 2. Incremental Goals

Break complex tasks into phases rather than attempting everything at once. Each phase should have its own completion marker.

### 3. Self-Correction Pattern

Use TDD or iterative refinement: write failing tests → implement → run tests → debug failures → refactor → repeat until all pass.

## When to Use Ralph

**Good For:**

- Well-defined tasks with clear success criteria
- Tasks requiring iteration and refinement (e.g., getting tests to pass)
- Greenfield projects where you can walk away
- Tasks with automatic verification (tests, linters)
- Overnight/weekend automated development

**Not Good For:**

- Tasks requiring human judgment or design decisions
- One-shot operations that need immediate results
- Tasks with unclear or subjective success criteria
- Production debugging
- Tasks requiring external approvals or human-in-the-loop

## Command Syntax

```
/ralph-loop:ralph-loop "<prompt>" --completion-promise "<text>" --max-iterations <n>
```

**Key Options:**

- `--completion-promise`: Phrase that signals completion (exact match required)
- `--max-iterations`: Stop after N iterations (recommended safety net)

## Prompt Writing Best Practices

### Template Structure

```
[Clear task description]

Requirements:
- [Requirement 1]
- [Requirement 2]
- [Requirement 3]

Success criteria:
- [Criterion 1]
- [Criterion 2]
- [Criterion 3]

Output <promise>COMPLETE</promise> when done.
```

### Escape Hatches

- Always use `--max-iterations` to prevent infinite loops
- Include stuck-handling instructions for when Ralph hits a wall
- Rely on `--max-iterations` as the primary safety mechanism

## Real-World Results

- **Y Combinator Hackathon**: 6 repositories generated overnight
- **$50k Contract**: Completed, tested, and reviewed for just $297 in API costs
- **CURSED Language**: Entire programming language created over 3 months using this approach

## Advanced Patterns

### Multi-Phase Development

Chain multiple Ralph loops for complex projects:

```
Phase 1: Build core models → PHASE1_DONE
Phase 2: Build API endpoints → PHASE2_DONE
Phase 3: Build UI → PHASE3_DONE
```

### Parallel Development with Git Worktrees

Run multiple Ralph loops on different branches simultaneously for features that don't conflict.

### Overnight Batch Processing

Queue up multiple Ralph loop commands to run unattended while you sleep.

### Prompt Tuning Technique

- Start with minimal guardrails
- When Ralph fails, add guardrails based on what went wrong
- Iterate until prompts are tuned and defects disappear

## Ready-to-Use Templates

### Feature Implementation

```
/ralph-loop:ralph-loop "Implement [FEATURE].
Requirements: [List requirements]
Success criteria: All requirements met, tests passing (>80% coverage), no linter errors, documentation updated.
Output <promise>COMPLETE</promise>" --max-iterations 30
```

### TDD Development

```
/ralph-loop:ralph-loop "Implement [FEATURE] using TDD.
Process: Write failing test → Implement → Run tests → Fix failures → Refactor → Repeat
Output <promise>DONE</promise>" --max-iterations 50
```

### Bug Fixing

```
/ralph-loop:ralph-loop "Fix bug: [DESCRIPTION].
Steps: Reproduce → Identify root cause → Implement fix → Write regression test → Verify
Output <promise>FIXED</promise>" --max-iterations 20
```

### Refactoring

```
/ralph-loop:ralph-loop "Refactor [COMPONENT] for [GOAL].
Constraints: All tests must pass, no behavior changes
Output <promise>REFACTORED</promise>" --max-iterations 25
```

## Accessing External Documentation

The Ralph Wiggum technique can be enhanced to include external URLs and workspace documentation in the prompt context:

### Including Workspace Documentation

Reference local documentation files in your prompt:

```
/ralph-loop:ralph-loop "Implement [FEATURE] following architecture guidelines.
Reference the following documentation:
- docs/architecture.md
- docs/api-patterns.md
- src/Shared/README.md

Requirements: [List requirements]
Success criteria: All requirements met, tests passing, follows documented patterns.
Output <promise>COMPLETE</promise>" --max-iterations 30
```

The AI agent will automatically load and reference these files from the workspace during execution.

### Including External URLs

Reference external documentation to inform implementation:

```
/ralph-loop:ralph-loop "Implement [FEATURE].
Reference external documentation:
- https://learn.microsoft.com/dotnet/api/system.threading.channels
- https://github.com/GuimoBallesteros/IEnumerableAsync

Requirements: [List requirements]
Success criteria: Implementation follows best practices from referenced resources.
Output <promise>COMPLETE</promise>" --max-iterations 30
```

### Best Practices for Documentation References

- **Be Specific**: Reference exact file paths or URL anchors (e.g., `#async-best-practices`)
- **Minimize Context**: Only include relevant documentation sections to keep token usage reasonable
- **Cache Documentation**: For frequently used docs, store key excerpts in the prompt template
- **Version Your References**: Include specific versions of external resources to prevent drift

## Related Resources

- [Official Plugin Repository](https://github.com/anthropics/claude-plugins-official/tree/main/plugins/ralph-loop)
- [Geoffrey Huntley's Blog](https://ghuntley.com/ralph/) - Original technique creator and philosophy
- [Ralph Orchestrator](https://github.com/mikeyobrien/ralph-orchestrator) - Community tool for managing Ralph loops
- [Awesome Claude](https://awesomeclaude.ai/) - Curated collection of Claude AI tools and resources
