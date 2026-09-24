# Milestone 1: Hermes-Style Agent Framework

**Status:** complete
**Started:** 2026-08-14
**Completed:** 2026-09-21

## Goal

Transform Daedalus from a Ralph Loop orchestrator into a Hermes-like, .NET-oriented
autonomous agent framework. The new framework
provides a general agentic core (agent loop, tool use, sessions) and integrates the
existing in-house libraries: **Rag.NET** (retrieval-augmented memory and knowledge)
and **AI.Sentinel** (security monitoring, detection, and approval workflows at the
model boundary).

**Scope change (2026-09-20):** retiring the Ralph Loop was part of this milestone and has
moved out of it, to phase 2.5. Ralph is not deleted as dead weight — the Wiggum loop is the
seed of the Milestone 2 software-manufacturing design, so it is redesigned rather than
removed, and it is switched off only once its replacement demonstrably does the job. Phase 1.7
keeps the three library migrations that were bundled with it. Milestone 1 therefore closes
with Ralph still running.

## Definition of Done

- [x] All planned phases complete — 1.1 through 1.9, all merged. Ralph retirement moved out of
      scope to phase 2.5 on 2026-09-20; the loop is redesigned as the seed of Milestone 2, not
      deleted, so this milestone closes with Ralph still running by deliberate decision.
- [x] All tests passing (`dotnet test`, all suites) — phase 1.7's merge left unit 1078,
      Integration 505, Playwright.Api 126, Playwright.Browser 99 all green, and no phase since has
      touched `src/`. Not re-run for this closing task per instruction; this ticks on the last
      verified run, not a fresh one.
- [x] Regression test PASS (web UI verified via Playwright with screenshots) — phase 1.1's
      `docs/regression-report-2026-08-16.md`, and phase 1.6's end-to-end browser proof through
      Keycloak with Resend exercised against the database for the diagnostics UI. No UI shipped
      after 1.6 without its own regression evidence.
- [x] Documentation complete (design + plan docs in `docs/plans/` for each phase) — true under
      this file's own operational definition: every phase 1.1–1.9 has a design and plan doc in
      `docs/plans/`. This does **not** claim a full-repo documentation audit. Phase 1.8 exists
      because `docs/architecture-diagrams.md` had drifted into active fiction — wrong interface
      signatures, the wrong LLM backend named throughout, non-existent entities — and that
      specific document is now rewritten with every symbol and package claim checked against
      source. Other documents outside this phase's scope were not swept for the same drift.

## Phases

The framework is built as a standalone library, **Thalos.NET** (separate repo
`C:\Projects\Prive\Thalos.NET`, published to nuget.org, ZeroAlloc-native, on Microsoft
Agent Framework 1.17). Daedalus consumes it. Design: `docs/plans/2026-08-16-thalos-agent-core-design.md`.

| # | Phase | Status |
|---|---|---|
| 1.1 | Thalos.NET core + AI.Sentinel + Daedalus HTTP/Blazor channel | complete (2026-08-17; Thalos.NET 0.1.1 on nuget.org, #227) |
| 1.2 | Memory: `Thalos.NET.Memory` port + Rag.NET adapter | complete (2026-08-17; Thalos.NET 0.2.0 on nuget.org, #228) |
| 1.3 | Skills: agent-scoped procedure documents | complete (2026-08-19; Thalos.NET 0.3.0 on nuget.org, #229) |
| 1.4 | Channels: Telegram (+ CLI) | complete (2026-08-22; Thalos.NET 0.4.0 on nuget.org, #241) |
| 1.5 | Subagents & autonomous runs (`ISubagentRunner` + outbox-driven steps, no Saga, no Scheduling) | complete (2026-09-18; plan A as Thalos.NET 0.5.0, plan B merged via #242) |
| 1.6 | Schedule diagnostics (Blazor page + read-only agent tools) | complete (2026-09-19) |
| 1.7 | Daedalus ZeroAlloc migration: FluentValidation → `ZeroAlloc.Validation`, CQRS → `ZeroAlloc.Mediator`, CSFE → `ZeroAlloc.Results` | complete (2026-09-21; #257) |
| 1.8 | Docs and architecture-diagrams rewrite | complete (2026-09-21; no Thalos.NET release — see below) |
| 1.9 | Scout repository tooling (GitHub read tools + policy-gated write tools) | complete (2026-09-19) |

**Phase 1.8 note:** the plan for this phase called for shipping Thalos.NET 0.6.0. That was wrong
and is cancelled. Thalos.NET releases via release-please from conventional commits, and since
0.5.1 there are 18 commits — 17 `chore`, 1 `docs`, zero `feat`, zero `fix` — none releasable
without forcing a `Release-As` override. Confirmed with the user: no override, because nothing in
the code changed and a minor version would signal features that do not exist. The documentation
is merged to Thalos.NET `main` and reaches the NuGet package page whenever the next real change
ships.

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
| 2026-09-21 | Complete | Documentation-complete tick is scoped to phase design/plan docs plus the `docs/architecture-diagrams.md` rewrite and Thalos.NET package README, not a full-repo audit. Seven carried-forward items below are known and deliberately unfixed. |

## Carried Forward

Real, found during phases 1.7–1.9, deliberately left open at close rather than fixed or hidden:

1. **Two pre-existing `Entity<TId>` equality bugs** — cross-type equality, and transient entities
   comparing equal. Pinned as clearly-labelled characterisation tests (phase 1.7).
2. **A `byte[] RowVersion` column is inert on Npgsql** on 7 older entities, so they have no working
   optimistic concurrency; only the 2 newer scheduling entities use the real `xmin` column. Known,
   with a code comment and a design-doc admission (found in phase 1.8).
3. **`GetAllTasksQuery` and `GetTaskByIdQuery` are dead** — registered handlers, never invoked;
   reads bypass the mediator via `ITaskQueryService` (found in phase 1.8).
4. **CI excludes three suites** — both Playwright projects outright, and the Keycloak tests by
   `Category!=AuthenticationFlow`. All three now pass (phase 1.7 proved it), but the filter itself
   is unchanged. This is how a suite sat at 0 of 126 passing unnoticed since phase 1.4.
5. **`ZA0501` suppressed** in `Directory.Build.props`, pending a repo-wide `[LoggerMessage]`
   migration.
6. **`benchmarks/` (`Daedalus.Benchmarks`) is absent from `Daedalus.sln`**, so CI never builds it;
   it has two pre-existing compile errors.
7. **Thalos.NET's `scripts/pack-local.ps1`** hard-codes `0.3.0-<suffix>` and never calls
   GitVersion, and `Directory.Build.props` `VersionPrefix` is stuck at 0.3.0. Real releases are
   safe because GitVersion wins in CI; local dev feeds are not.
