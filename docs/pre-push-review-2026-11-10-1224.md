# Pre-push review: phase 3.5, EF Core to ZeroAlloc.ORM

| Metric | Value |
|---|---|
| Date | 2026-11-10 12:24 |
| Branch | phase/3.5-zeroalloc-orm |
| Base Branch | main |
| Commits Reviewed | 86 |
| Files Changed | 404 |
| Lines Added | 19776 |
| Lines Removed | 38484 |
| Verdict | **PASS** |

## Plan adherence

- Plan: `docs/superpowers/plans/2026-11-05-phase-3.5-zeroalloc-orm-plan.md`, Tasks 1 to 25 ticked, Task 26 in progress.
- Every task had a task review with spec and quality verdicts, recorded in the SDD ledger. Decisions are the rulings
  R1 to R136 in `docs/plans/2026-11-05-phase-3.5-rulings.md`, each with its cost if wrong.
- Unplanned changes: the final fix wave (migration history upgrade order, two unwritten columns, doc and comment fixes),
  from the whole-branch review. Info.

## Code quality

- Whole-branch review on the most capable model: 0 Critical, 2 Important, 3 Minor. I1 (migration history upgrade ran
  after the Daedalus runner) is fixed with two host tests. I2 (R117, ScheduleReconciler start failure) is filed as #444.
  M1 scalar column writes fixed; the child writes of Project.AddTask and RemoveTask are commented on #377. M2 and M3 fixed.
- A scoped re-review of the fix wave passed all findings; its Minor items were fixed in ee3acdab or are cosmetic line wraps in tests.
- Lost-save detection by text match is filed as #445. No finding is neither fixed nor filed.

## Commit hygiene

- Signatures or Co-Authored-By: 0. Body lines over 100 characters: 0. Nested parentheses in bodies: 0.
- Conflict markers: 0. Secret patterns in added non-doc lines: 0. New files over 3000 lines: none.
- All subjects follow the conventional commit format.

## Regression tests

Local, head bdf2ca59 plus the fix wave, Release build 0 warnings and 0 errors:
Unit.Domain 382, Unit.Application 309, Unit.Infrastructure 231, Unit 2452, Playwright.Api 115 pass.
Integration 1002 pass and 3 fail: `RunScopedRoslynRoutingTests`, which fail only on the agents VM (#335).
After the fix wave: Integration Migrations and Repositories filter 174 pass, Migrations filter 23 pass.
Playwright.Browser is judged by CI. Native publish for Api, Cli and Migrations passes; the native CLI reaches
"Application started" (R136). The native turn was not tried: no API key on the VM.

Warnings: 1 (the 3 VM-only failures count as known, not a blocker). No blockers.
