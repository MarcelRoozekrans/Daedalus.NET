# Handover: why Daedalus wants ZeroAlloc.Linq, compile-time LINQ to SQL

**From:** the Daedalus session running milestone 3, Native AOT
**To:** the agent building ZeroAlloc.Linq and the ZeroAlloc.ORM gaps in the ZeroAlloc-Net org
**Date:** 2026-10-17
**Owner decision:** ZeroAlloc.ORM gets a compile-time LINQ layer, ZeroAlloc.Linq, built in its own session alongside
the ORM gaps listed below.
**Corrected:** 2026-10-21, from section 6 of the gap audit, `docs/plans/2026-10-17-zeroalloc-orm-gap-audit.md`. The
audit is the full record, with a file:line for every use. Where this handover and the audit differ, the audit wins.

## The problem in one paragraph

Daedalus must ship as Native AOT, and EF Core has no roadmap there: EF's query pipeline builds and compiles
expression trees at run time. So the data layer moves to ZeroAlloc.ORM, which is AOT-clean, source-generated and
free of runtime reflection. But ZeroAlloc.ORM is **raw SQL only**. Its README says there is "no LINQ provider, no
expression compilation and no runtime reflection." Moving Daedalus as things stand means rewriting every EF query as
hand-written SQL in `[Query]`/`[Command]` attributes: about 117 statements over the 22 files that run queries, out of
32 that name the `DbContext`, including grouped cost aggregations, eager loading and keyset paging.

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

This comes from recon of Daedalus `main` `8173fd55` on 2026-10-17, corrected from the gap audit. Paths are relative
to `MarcelRoozekrans/Daedalus.NET`. The gap audit, `docs/plans/2026-10-17-zeroalloc-orm-gap-audit.md`, gives a
file:line for every use: section 5.1 is the full query-shape catalogue, and section 6 lists what this handover got
wrong or left out.

### Query shapes to cover, most used first

- **Filters:**
  - equality, range and `null` checks, combined with `&&` and `||`;
  - membership in a captured list (`Contains`);
  - its negation, `<> ALL(@array)`, in `PostgresSkillStore.cs:213` and `PostgresRoleCharterStore.cs:106`;
  - membership in an array column, `@tag = ANY("Tags")`, through `EF.Property` on a private backing field, in
    `PostgresSkillStore.cs:183` and `PostgresMemoryStore.cs:268`;
  - a predicate on an owned type's member, `r.Repository.Url`, in `CodeAnalysisRepository.cs:94`;
  - `string.Length`, in `FailurePatternDatabase.cs:52`;
  - a correlated `NOT EXISTS`, `!Any(...)` for the latest row per group, in `ScheduleDiagnostics.cs:116-117`;
  - conditional composition, a `Where` added only when an option is set, in `WorkflowRunRecordStore.cs:31-32`,
    `PostgresSkillStore.cs:165-190` and `PostgresMemoryStore.cs:239-280`;
  - case-insensitive matching: `EF.Functions.ILike` in `FailurePatternDatabase.cs:32`;
  - enum comparisons, stored as `int` or `string`.
- **Ordering and paging:**
  - `OrderBy`/`ThenBy`/`Descending`, `Take`/`Skip`;
  - **keyset paging on a tuple**, `(CreatedAt, Id) > (@a, @b)`, in `PostgresMemoryStore.cs:210`.
- **Projections:** `Select` into DTOs and anonymous types, and `Count`, `Any` and `FirstOrDefault`.
- **Scalar aggregates:** `MaxAsync` over `int?` in `PostgresAgentSessionStore.cs:130`, and `AverageAsync` over
  `double?` in `CostAnalyticsService.cs:274`.
- **Aggregation,** in `src/Daedalus.Infrastructure/Services/CostAnalyticsService.cs`:
  - `GroupBy` on anonymous keys with `Sum`/`Count` (`:80`, `:144`);
  - date truncation on the key (`.Date`);
  - a **two-way join** Executions ⋈ Tasks ⋈ Projects followed by `GroupBy` (`:194-212`);
  - a **single join** of Executions onto a filtered `Tasks`, where `ProjectId` matches, followed by `GroupBy`
    (`:235-249`).

  The last lines of both join queries, `:214-215` and `:251-252`, are in-memory LINQ over the results, not SQL.
- **Eager loading:**
  - `Include` in `TaskRepository.cs:24`, `TaskQueryService.cs:19,38`, `ProjectQueryService.cs:23,79` and
    `CodeAnalysisRepository.cs:24`;
  - **`Include` on a paged query**, before `OrderBy`/`Skip`/`Take`, in `TaskQueryService.cs:19` and
    `ProjectQueryService.cs:23`;
  - **filtered and ordered `Include`**, `Include(s => s.Messages.OrderBy(...))`, in `BrainstormRepository.cs:38`.

  ZeroAlloc.ORM's multi-result `(Head, Lines)` queries are the natural target.
- **Set-based writes:** `ExecuteUpdate`/`ExecuteDelete` with predicates and computed setters, in PostgresSkillStore,
  PostgresMemoryStore, AgentSessionCrashRecovery, PostgresAgentSessionStore, PostgresConversationMap and
  PostgresRoleCharterStore.
- **Row locking:** `SELECT … FOR UPDATE` inside a transaction, in `PostgresAgentSessionStore.cs:115-119`. A LINQ
  hook such as `.ForUpdate()` would cover it; raw SQL is an acceptable fallback here.
- **Raw upserts:** `ON CONFLICT DO UPDATE` in `PostgresConversationMap.cs:81-87`, and `ON CONFLICT DO NOTHING` in
  `ScheduledRunExecutionStore.cs:120-125,186-193`. Raw SQL is fine for these too.
- **Optimistic concurrency:**
  - `xmin` (PostgreSQL's system column) as a `uint` token, on Task, ScheduledRun and ScheduledRunExecution;
  - updates must be guarded with `WHERE … AND xmin = @expected`, and report zero rows as a conflict;
  - the ORM can neither read a `uint` nor bind one as `xid` today (audit 4.4), so this needs unsigned integers and
    a data-type name on the parameter;
  - the six `IsRowVersion()` `bytea` tokens enforce nothing: PostgreSQL never generates them (audit 3.4). They are
    a Daedalus defect, Daedalus.NET#290, which 3.5 settles. ZeroAlloc.Linq does not need to support them.
- **Transactions:** queries must run on a caller's `IAsyncDbTransaction`. Today several stores share one transaction
  with the EF outbox store; after the move that will be `ZeroAlloc.Outbox.Orm`.

### Types and columns to handle

- **jsonb** columns: seven, on CodeAnalysisRequest, AnalysisIteration, AgentMessage and WorkflowRunRecord. Every one
  is a C# `string` holding a JSON document, so the need is binding a `string` parameter as jsonb and reading it back
  with `GetString`. They hold no owned objects: the owned types are table-split into ordinary columns
  (`CodeAnalysisRequestConfiguration.cs:26-78`). Querying inside jsonb is not needed today.
- **`text[]` arrays:** Skill, SkillVersion, RoleCharterVersion, AgentMemory and Task. They need read, write, and an
  `unnest` or `ANY(@array)` comparison.
- **Enums, `DateTimeOffset`, `Guid` and nullable primitives.** No Daedalus column is a typed id: ids are `Guid`
  columns, unwrapped at the store boundary, so no typed-id converter is needed.
- **The existing schema exactly as EF created it,** including EF's column names. No table may be renamed or
  reshaped.

### The ORM gaps

The audit confirmed the three gaps this handover first named, and found **three more**, so the list is six. They
are needed whatever the query layer looks like. Details, evidence and stopgaps are in audit section 4.

1. **Adopting an existing database.** `MigrationRunner` only knows `__zaorm_migrations`. Daedalus has 26 EF migrations
   recorded in `__EFMigrationsHistory`. It needs a baseline or adoption path, so existing databases upgrade in place
   with no data loss (an owner decision). Audit 4.1.
2. **Array parameters and array columns.** The ORM's reference docs, `docs/README.md:199`, list them as not
   supported; the 2.3.4 root README no longer says so. Audit 4.2.
3. **jsonb parameters,** as a real parameter type rather than a `string` with a `::jsonb` cast. Audit 4.3.
4. **Unsigned integers,** for the `xmin` token, read as `uint` and bound as `xid`. Audit 4.4.
5. **In `ZeroAlloc.Outbox.Orm`:** no `IOutboxDashboardStore`, which Daedalus' resend needs, and a fixed table name.
   Audit 4.5.
6. **A defect:** a migration file with a 14-digit version is skipped silently. Audit 4.6.

**What blocks what.** By the audit's section 5, two of the six block part of 3.5 outright, and the others have
stopgaps:
- gap 2, arrays, blocks every store that touches a `text[]` column or a `Contains` filter;
- gap 5 blocks the outbox move and the resend;
- gaps 1, 3, 4 and 6 have stopgaps on the Daedalus side, so they block nothing, but each stopgap is a workaround to
  remove once the gap closes.

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
- The six gaps above are fixed and released.
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
- The ORM gap audit: `docs/plans/2026-10-17-zeroalloc-orm-gap-audit.md`. Section 5.1 is the query-shape catalogue,
  and section 6 the corrections to this handover.

**Existing ZeroAlloc.ORM consumers,** for patterns:
- `ZeroAlloc.Outbox.Orm`, for example `ClaimPostgresPendingOutboxQuery`;
- `ZeroAlloc.Saga.Orm`, for example `SagaInstanceRepository` and its RowVersion guard.

**Contact:** the Daedalus session can answer questions about any query above and test against Daedalus' integration
suite of 749 tests on PostgreSQL.
