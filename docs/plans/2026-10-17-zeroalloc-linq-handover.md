# Handover: why Daedalus wants ZeroAlloc.Linq, compile-time LINQ to SQL

**From:** the Daedalus session running milestone 3, Native AOT
**To:** the agent building ZeroAlloc.Linq and the ZeroAlloc.ORM gaps in the ZeroAlloc-Net org
**Date:** 2026-10-17
**Owner decision:** ZeroAlloc.ORM gets a compile-time LINQ layer, ZeroAlloc.Linq, built in its own session alongside
the three ORM gaps listed below.

## The problem in one paragraph

Daedalus must ship as Native AOT, and EF Core has no roadmap there: EF's query pipeline builds and compiles
expression trees at run time. So the data layer moves to ZeroAlloc.ORM, which is AOT-clean, source-generated and
free of runtime reflection. But ZeroAlloc.ORM is **raw SQL only**. Its README says there is "no LINQ provider, no
expression compilation and no runtime reflection." Moving Daedalus as things stand means rewriting every EF query as
hand-written SQL in `[Query]`/`[Command]` attributes: about 32 source files, including grouped cost aggregations,
eager loading and keyset paging.

**ZeroAlloc.Linq closes that gap the AOT way.** It translates LINQ queries to SQL **at compile time**, in a source
generator, and executes them through ZeroAlloc.ORM. Nothing builds or compiles an expression tree at run time.

## Why compile-time LINQ, and not the alternatives

| Option | Why not, or why |
|---|---|
| Stay on EF Core | Not AOT-capable, which is the whole reason for the milestone. Precompiled queries are experimental, and the runtime still reflects |
| Hand-written SQL everywhere | Works with ZeroAlloc.ORM today, but loses the readability and refactor-safety Daedalus has: renames, typed predicates, compile errors on schema drift. It is also a large one-off rewrite whose SQL then has to be maintained by hand |
| A runtime LINQ provider | Needs expression trees, `Expression.Compile` or reflection, which are exactly what AOT forbids |
| A specification or query-builder API | Smaller, but unfamiliar to write. Every query becomes a new DSL instead of the LINQ the code already has |
| **Compile-time LINQ (ZeroAlloc.Linq)** | **Keeps queries as LINQ, close to today's EF code. The generator sees the query shape at build time, emits SQL plus a materialiser, and fails the build on anything it can't translate. AOT-clean by construction, and reusable by any ZeroAlloc.ORM consumer** |

A plausible mechanism is C# interceptors: the generator intercepts the LINQ call chain on a known query root, with
fixed parameters as captured locals, and emits the SQL. This is a suggestion only; the design is yours.

## What Daedalus actually needs

This comes from recon of Daedalus `main` `8173fd55` on 2026-10-17. Paths are relative to
`MarcelRoozekrans/Daedalus.NET`. Phase 3.1's gap audit (`docs/plans/2026-10-17-zeroalloc-orm-gap-audit.md`, being
written) will add file:line for every use.

### Query shapes to cover, most used first

- **Filters:**
  - equality, range and `null` checks, combined with `&&` and `||`;
  - membership in a captured list (`Contains`);
  - case-insensitive matching: `EF.Functions.ILike` in `FailurePatternDatabase.cs:32`;
  - enum comparisons, stored as `int` or `string`.
- **Ordering and paging:**
  - `OrderBy`/`ThenBy`/`Descending`, `Take`/`Skip`;
  - **keyset paging on a tuple**, `(CreatedAt, Id) > (@a, @b)`, in `PostgresMemoryStore.cs:210`.
- **Projections:** `Select` into DTOs and anonymous types, and `Count`, `Any` and `FirstOrDefault`.
- **Aggregation,** in `src/Daedalus.Infrastructure/Services/CostAnalyticsService.cs`:
  - `GroupBy` on anonymous keys with `Sum`/`Count` (`:80`, `:144`);
  - date truncation on the key (`.Date`);
  - a **two-way join** Executions ⋈ Tasks ⋈ Projects followed by `GroupBy` (`:194-215`, `:239-252`).
- **Eager loading:**
  - `Include` in `TaskRepository.cs:24`, `TaskQueryService.cs:19,38`, `ProjectQueryService.cs:23,79` and
    `CodeAnalysisRepository.cs:24`;
  - **filtered and ordered `Include`**, `Include(s => s.Messages.OrderBy(...))`, in `BrainstormRepository.cs:38`.

  ZeroAlloc.ORM's multi-result `(Head, Lines)` queries are the natural target.
- **Set-based writes:** `ExecuteUpdate`/`ExecuteDelete` with predicates and computed setters, in PostgresSkillStore,
  PostgresMemoryStore, AgentSessionCrashRecovery, PostgresAgentSessionStore, PostgresConversationMap and
  PostgresRoleCharterStore.
- **Row locking:** `SELECT … FOR UPDATE` inside a transaction, in `PostgresAgentSessionStore.cs:115-119`. A LINQ
  hook such as `.ForUpdate()` would cover it; raw SQL is an acceptable fallback here.
- **Optimistic concurrency:**
  - `xmin` (PostgreSQL's system column) and `IsRowVersion()` byte tokens;
  - updates must be guarded with `WHERE … AND xmin = @expected`, and report zero rows as a conflict.
- **Transactions:** queries must run on a caller's `IAsyncDbTransaction`. Today several stores share one transaction
  with the EF outbox store; after the move that will be `ZeroAlloc.Outbox.Orm`.

### Types and columns to handle

- **jsonb** columns, holding owned objects and payload documents: CodeAnalysisRequest, AnalysisIteration,
  AgentMessage and WorkflowRunRecord. Reading and writing a whole document is enough; querying inside jsonb is not
  needed today.
- **`text[]` arrays:** Skill, SkillVersion, RoleCharterVersion, AgentMemory and Task. They need read, write, and an
  `unnest` or `ANY(@array)` comparison.
- **Typed ids** (`ZeroAlloc.ValueObjects`), enums, `DateTimeOffset`, `Guid` and nullable primitives.
- **The existing schema exactly as EF created it,** including EF's column names. No table may be renamed or
  reshaped.

### The three ORM gaps

These block the migration whatever the query layer looks like:

1. **Adopting an existing database.** `MigrationRunner` only knows `__zaorm_migrations`. Daedalus has 26 EF migrations
   recorded in `__EFMigrationsHistory`. It needs a baseline or adoption path, so existing databases upgrade in place
   with no data loss (an owner decision).
2. **Array parameters,** which the README lists as not supported.
3. **jsonb parameters,** as a real parameter type rather than a `string` with a `::jsonb` cast.

## Hard constraints

- **AOT-clean:** zero trim or AOT warnings, with no suppressions. Daedalus' CI ratchets every target's warning count,
  and the milestone allows no `UnconditionalSuppressMessage`.
- **Compile-time failure:** a LINQ shape the generator can't translate must be a **build error** with a clear
  diagnostic. It must never fall back silently to client evaluation or run-time reflection.
- **PostgreSQL first.** Daedalus runs on PostgreSQL 16 with pgvector loaded. Other dialects are your call.
- **Plain SQL:** the SQL produced should be readable and inspectable, for example as a generated file or a debug
  view, and parameterised. No string concatenation of values.
- **Reusable:** it should work for any ZeroAlloc.ORM consumer. Daedalus is the first big one; Thalos'
  `Workflow.Orm` is a possible second.

## What "done enough" looks like for Daedalus

- Every query shape above compiles to SQL, runs on ZeroAlloc.ORM, and is covered by tests in your repo against a real
  PostgreSQL.
- The three gaps are fixed and released.
- A released package Daedalus can reference. Daedalus phase 3.5, EF Core to ZeroAlloc.ORM, waits on this and
  consumes it.

## Non-goals, from Daedalus' side

- **Change tracking**, unit-of-work or lazy loading. Daedalus can move to explicit writes.
- **Querying inside jsonb documents.**
- **Migrations generated from the model.** Hand-written or embedded SQL migrations, as ZeroAlloc.ORM does today, are
  fine.
- **EF API compatibility.** It should feel like LINQ, not be a drop-in replacement for EF.

## Where to look

**Daedalus:**
- The milestone design: `docs/superpowers/specs/2026-10-17-milestone-3-design.md`.
- The AOT survey: `docs/plans/2026-10-17-native-aot-survey.md`.
- The phase 3.1 design: `docs/superpowers/specs/2026-10-17-phase-3.1-aot-foundation-design.md`.
- The ORM gap audit, once written: `docs/plans/2026-10-17-zeroalloc-orm-gap-audit.md`.

**Existing ZeroAlloc.ORM consumers,** for patterns:
- `ZeroAlloc.Outbox.Orm`, for example `ClaimPostgresPendingOutboxQuery`;
- `ZeroAlloc.Saga.Orm`, for example `SagaInstanceRepository` and its RowVersion guard.

**Contact:** the Daedalus session can answer questions about any query above and test against Daedalus' integration
suite of 745 tests on PostgreSQL.
