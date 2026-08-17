# Brainstorm Sessions Design

**Goal:** Replace the one-shot PRD generation with an interactive, phased brainstorming conversation (inspired by superpowers skills) that produces higher-quality design docs, implementation plans, and phased tasks before Ralph executes autonomously.

**Architecture:** Conversation-per-Session — a new `BrainstormSession` aggregate root with a state machine tracking 6 explicit phases. The API exposes a chat-like interface; the Blazor frontend renders it as an interactive conversation.

**Tech Stack:** ASP.NET Core API, EF Core (PostgreSQL), Blazor WASM, existing `IRalphAgentFactory` for LLM calls, MCP tools for learnings/repo access.

---

## Domain Model

### BrainstormSession (Aggregate Root)

| Property             | Type                      | Description                                      |
|----------------------|---------------------------|--------------------------------------------------|
| Id                   | Guid                      | Primary key                                      |
| ProjectId            | Guid                      | Associated project                               |
| Phase                | BrainstormPhase (enum)    | Current phase of the conversation                |
| Messages             | List\<BrainstormMessage\> | Full conversation history                        |
| DesignDocument       | string?                   | Approved design (markdown), set in DesignReview  |
| ImplementationPlan   | string?                   | Generated plan (markdown), set in PlanGeneration |
| PhaseCompleteSignaled| bool                      | LLM has signaled readiness to advance            |
| CreatedAt            | DateTime                  | Session creation timestamp                       |
| CompletedAt          | DateTime?                 | Terminal state timestamp                         |
| RowVersion           | byte[]                    | Optimistic concurrency token                     |

### BrainstormPhase (Enum)

| Value | Name             | Description                                          |
|-------|------------------|------------------------------------------------------|
| 0     | ContextGathering | LLM reads project, repo structure, past learnings    |
| 1     | Clarification    | LLM asks questions one at a time (multiple choice)   |
| 2     | Proposals        | LLM proposes 2-3 approaches with trade-offs          |
| 3     | DesignReview     | LLM presents design section by section, user approves|
| 4     | PlanGeneration   | LLM generates TDD implementation plan with phases    |
| 5     | TaskCreation     | Plan parsed into phased tasks with dependencies      |
| 6     | Completed        | Terminal state                                       |
| 7     | Abandoned        | User cancelled                                       |

Phase transitions are forward-only (or to Abandoned). Guarded by domain logic.

### BrainstormMessage (Value Object)

| Property  | Type            | Description                        |
|-----------|-----------------|------------------------------------|
| Id        | Guid            | Message identifier                 |
| Role      | MessageRole     | System, Assistant, or User         |
| Content   | string          | Message text (markdown)            |
| Phase     | BrainstormPhase | Which phase this message belongs to|
| CreatedAt | DateTime        | Timestamp                          |

---

## API Design

### BrainstormController

| Method | Endpoint                                      | Auth Policy     | Description                                                |
|--------|-----------------------------------------------|----------------|------------------------------------------------------------|
| POST   | /api/brainstorm/sessions                      | CodeAnalysis   | Create session, trigger ContextGathering automatically     |
| GET    | /api/brainstorm/sessions/{sessionId}          | CodeAnalysisRead| Get full session with conversation history                |
| POST   | /api/brainstorm/sessions/{sessionId}/messages | CodeAnalysis   | Send user message, get LLM response                       |
| POST   | /api/brainstorm/sessions/{sessionId}/advance  | CodeAnalysis   | Confirm phase transition (after LLM signals readiness)    |
| POST   | /api/brainstorm/sessions/{sessionId}/abandon  | CodeAnalysis   | Mark session as Abandoned                                 |
| POST   | /api/brainstorm/sessions/{sessionId}/generate-tasks | TaskManagement | Parse plan into tasks (TaskCreation phase only)     |
| GET    | /api/brainstorm/sessions?projectId={id}       | CodeAnalysisRead| List sessions for a project                              |

### Key Behaviors

- **Session creation** triggers ContextGathering automatically. The first assistant message is the LLM's context summary.
- **`/messages`** is synchronous request-response. No WebSockets needed.
- **Phase advancement is explicit** — the LLM signals readiness with `[PHASE_COMPLETE]` marker in its response, but the user must call `/advance` to confirm. Prevents the LLM from rushing.
- **`/generate-tasks`** reuses the existing `ConvertPrdToTasksCommandHandler` — no duplication.

---

## Phase-Specific System Prompts

Each phase gets a tailored system prompt controlling LLM behavior:

### ContextGathering
```
You are starting a brainstorming session for project '{name}'.
Here is the project context:
- Project: {metadata}
- Repository structure: {file tree from repo}
- Past learnings: {from MCP knowledge base}

Summarize what you understand about this project. Identify any
gaps in your understanding. End with [PHASE_COMPLETE] when done.
```

### Clarification
```
You are gathering requirements from the user. Ask ONE question
at a time. Prefer multiple-choice questions (2-4 options) when
possible. Focus on: purpose, constraints, success criteria,
scope boundaries. Do NOT propose solutions yet.

When you have enough information to propose approaches,
end your message with [PHASE_COMPLETE].
```

### Proposals
```
Based on the conversation so far, propose 2-3 implementation
approaches. For each approach include: name, description,
trade-offs (pros/cons), and your recommendation with reasoning.
Lead with your recommended approach.

End with [PHASE_COMPLETE] when you've presented all approaches.
```

### DesignReview
```
The user has chosen an approach. Present the design in sections:
architecture, components, data flow, error handling, testing
strategy. Present ONE section at a time. Ask if it looks right
before moving to the next section.

When all sections are approved, end with [PHASE_COMPLETE].
```

### PlanGeneration
```
Generate a detailed implementation plan from the approved design.
Break it into bite-sized tasks (2-5 minutes each). Follow TDD:
write failing test -> verify it fails -> implement -> verify it
passes -> commit. Include:
- Exact file paths (create/modify)
- Complete code snippets
- Test commands with expected output
- Dependency ordering and parallel groups
- Phase labels (e.g., 'Backend', 'Frontend', 'Integration')

Output the plan as structured markdown.
End with [PHASE_COMPLETE] when the plan is complete.
```

### Context Window Management
- Each LLM call includes full conversation history for the session
- Phase-specific system prompt is prepended
- MCP tools (learnings, failure patterns) are attached for on-demand queries
- `[PHASE_COMPLETE]` marker is stripped from stored messages; a `PhaseCompleteSignaled` flag is set on the session

---

## Blazor UI

### Layout

```
┌─────────────────────────────────────────────────┐
│  Phase Indicator Bar                            │
│  [*Context] [oClarify] [oPropose] [oDesign]     │
│  [oPlan] [oTasks]                               │
├─────────────────────────────────────────────────┤
│                                                 │
│  Message History (scrollable)                   │
│  ┌───────────────────────────────────────────┐  │
│  │ Assistant: I've reviewed project X...     │  │
│  ├───────────────────────────────────────────┤  │
│  │ You: We need a caching layer for...       │  │
│  ├───────────────────────────────────────────┤  │
│  │ Assistant: Which caching strategy?        │  │
│  │    A) Redis distributed cache             │  │
│  │    B) In-memory with IMemoryCache         │  │
│  │    C) Hybrid (memory + Redis fallback)    │  │
│  └───────────────────────────────────────────┘  │
│                                                 │
├─────────────────────────────────────────────────┤
│  [Advance to Next Phase >]  (when LLM signals)  │
├─────────────────────────────────────────────────┤
│  ┌─────────────────────────────────┐  [Send]   │
│  │ Type your message...            │            │
│  └─────────────────────────────────┘            │
└─────────────────────────────────────────────────┘
```

### Key UI Behaviors
- **Phase indicator bar** — current phase highlighted, completed phases filled
- **Auto-scroll** — new messages scroll into view
- **"Advance" button** — only visible when LLM has signaled `[PHASE_COMPLETE]`
- **Loading state** — typing indicator while waiting for LLM
- **Markdown rendering** — assistant messages rendered as markdown
- **TaskCreation phase** — replaces chat with task review screen (reuses existing PRD item selection UI) plus "Create Tasks" button

### Integration with Existing UI
- PRD page gets a "Start Brainstorming Session" option alongside quick-generate
- After tasks are created, they appear in the existing task dashboard
- Ralph picks them up autonomously — no change to execution pipeline

---

## Data Flow (End-to-End)

```
User clicks "Start Brainstorming" on project page
    |
    v
POST /api/brainstorm/sessions { projectId }
    |
    +-- Create BrainstormSession (Phase: ContextGathering)
    +-- Load project metadata from DB
    +-- Load repo structure via IGitRepositoryManager
    +-- Query MCP knowledge base (learnings + failure patterns)
    +-- Call LLM with ContextGathering system prompt + context
    +-- Store assistant message, detect [PHASE_COMPLETE]
    +-- Return session with first assistant message
    |
    v
User reads context summary, clicks "Advance"
    |
    v
POST /advance -> Phase: Clarification
    |
    v
  Conversation loop (Clarification)
  User answers -> LLM asks next question
  Until LLM signals [PHASE_COMPLETE]
    |
    v
POST /advance -> Phase: Proposals
  LLM proposes 2-3 approaches
  User discusses, picks one
    |
    v
POST /advance -> Phase: DesignReview
  LLM presents design section by section
  User approves/revises each section
  Design stored in session.DesignDocument
    |
    v
POST /advance -> Phase: PlanGeneration
  LLM generates detailed implementation plan
  (TDD steps, file paths, phases, parallel groups, dependencies)
  Plan stored in session.ImplementationPlan
    |
    v
POST /advance -> Phase: TaskCreation
  UI shows plan items for review
  User selects items, clicks "Create Tasks"
    |
    v
POST /generate-tasks
  Parse plan into PrdItemForConversionDto list
  Reuse ConvertPrdToTasksCommandHandler
  Phase: TaskCreation -> Completed
  Return created TaskDtos
    |
    v
Tasks appear in dashboard (Status: Pending)
Ralph Loop picks them up autonomously
```

---

## What Changes vs. What Stays

### Unchanged
- Task entity, TaskAssignmentService, Ralph Loop Worker
- ConvertPrdToTasksCommandHandler (reused as-is)
- MCP tools, middleware pipeline
- Existing quick-generate PRD flow (still available as alternative)

### New
- `BrainstormSession` + `BrainstormMessage` domain entities
- `BrainstormController` (7 endpoints)
- `IBrainstormService` + `BrainstormService` (conversation orchestration)
- `IBrainstormRepository` + `BrainstormRepository` (EF Core persistence)
- Phase-specific system prompt templates (6 prompts)
- Blazor `BrainstormPage` component
- EF Core migration for `BrainstormSessions` + `BrainstormMessages` tables
