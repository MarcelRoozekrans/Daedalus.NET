# Milestone 2 Audit: Software Manufacturing

**Date:** 2026-10-17
**Audited at:** `main` `15ab22e5` (#391)
**Definition of done:** `docs/planning/MILESTONE.md`, confirmed by the owner on 2026-09-24
**Verdict:** FAIL. Two criteria are not met: spend is not bounded, and phase 2.1 has no rulings record.

## Criteria

| # | Criterion | Result | Evidence |
|---|---|---|---|
| 1 | All planned phases complete | Met | `ROADMAP.md` marks 2.1 to 2.14 complete, each with a merged PR; the last two are #376 (2.13) and #388 (2.14) |
| 2 | One manufacture run proven live, intent to reviewed PR | Met | Already met 2026-10-01: run `dbf7b675`, daedalus-sandbox PR #7 (phase 2.5 rulings) |
| 3 | Ralph retired only after that run | Met | Already met 2026-10-10: PR #329, run `b6fd3a6b` (phase 2.8 rulings) |
| 4 | Agent output cannot execute on the host | Met | Already met 2026-10-04: run `ac684402` and the two marker-file tests (phase 2.6 rulings) |
| 5 | A run is traceable | Met | Already met 2026-10-11: run `4949a215`, PR #336 (phase 2.9 rulings) |
| 6 | Spend is visible and bounded | **Not met** | Visible: yes. Bounded: no. See "Gaps" |
| 7 | All tests passing | Met | All seven suites green on `15ab22e5`, see "Tests" |
| 8 | Documentation complete | **Not met** | Phase 2.1 has a design and a plan but no rulings record. See "Gaps" |
| — | Release will tag correctly | Skipped | `CONVENTIONS.md`: `Milestone completion tags a release: no`; release-please owns releases |

## Tests

Run on `main` `15ab22e5`, 2026-10-17, with `DOTNET_USE_POLLING_FILE_WATCHER=1`. The build has 0 warnings and 0 errors.

| Suite | Passed | Failed |
|---|---|---|
| Unit | 1984 | 0 |
| Unit.Domain | 356 | 0 |
| Unit.Application | 276 | 0 |
| Unit.Infrastructure | 258 | 0 |
| Integration | 738 | 0 |
| Playwright.Api | 115 | 0 |
| Playwright.Browser | 219 | 0 |

Without the polling watcher, the integration suite fails 125 tests on this VM because the per-user inotify limit is 128.
That is the machine's limit, not a defect in the code.

## Gaps

### 1. Spend is not bounded (criterion 6)

The criterion asks that the token budget "either caps spend during a turn or is documented as the post-turn check it
is, sized from measurements rather than picked". Neither holds:

- `Thalos:DetachedRuns:MaxTotalTokens` is 150,000. `BudgetedSubagentRunner` stamps it onto every workflow turn.
- `src/Daedalus.Api/appsettings.json:37-43` says it in capitals: "NOT SIZED FOR A MANUFACTURING IMPLEMENT TURN". One
  measured `implement` turn used 358,703 input tokens, and another 737,634.
- Sizing is open as #280, which has had no activity since 2026-09-25. The check runs after the turn has already
  spent. That half is Thalos.NET#173, outside this repository.
- The README and the development guide do not describe the budget, so it is not "documented as the post-turn check
  it is".

Visibility is met. Since phase 2.8, cost analytics counts each manufacture node's usage once, per model, with its
cache split. Chat and scheduled sessions are counted as unattributed tokens and are not priced.

> **Note, 2026-10-17 (phase 2.15):** the second bullet's "post-turn check" is wrong. The cap has been in-turn since
> Thalos 0.11.0: a turn that passes its ceiling is stopped mid-turn with `SubagentBudgetExceeded`. The gap was its
> sizing and its documentation, and phase 2.15 closed both. Rulings and measurements:
> `docs/plans/2026-10-17-phase-2.15-rulings.md`.

### 2. Phase 2.1 has no rulings record (criterion 8)

Phases 2.2 to 2.14 each have a design, a plan and a rulings record. Phase 2.1 has
`docs/plans/2026-09-21-phase-2.1-git-write-tooling-design.md` and its plan, but no rulings record. The rulings
practice started with 2.2. 2.1 was built with the owner present, and its decisions were recorded in the design doc,
such as the placement decision in section 2.

A smaller deviation, not counted as a gap: from 2.6 on, designs and some plans live in `docs/superpowers/specs/` and
`docs/superpowers/plans/`, not `docs/plans/`. The criterion names `docs/plans/`. Every document exists; only the
folder moved when the superpowers layout changed.

## Warnings (not counted toward the verdict)

- **No pre-push review reports in this milestone.** The newest `docs/pre-push-review-*.md` is dated 2026-08-21,
  before the milestone started on 2026-09-21. Phases 2.2 onwards were reviewed differently: a reviewer per task, and a
  whole-branch review recorded in the rulings of every phase except 2.3 and 2.10. Those two record per-task reviews only.
- **No live regression sweep since #391.** The last sweep, `docs/regression-report-2026-10-16-1745.md`, found no
  regressions from 2.14. Since then, #391 changed the model behind PRD generation and brainstorm and the Costs
  estimator's start. #391 is test-proven but has not been run against Anthropic, so PRD generation and brainstorm,
  which the sweep could not test, are still unconfirmed live.

## Closing the gaps

| Gap | Proposed closure | Size |
|---|---|---|
| 1. Spend bounded | Either size the ceiling from measurements (#280) and document it as a post-turn check, or have Thalos cap during the turn (Thalos.NET#173) | A phase |
| 2. 2.1 rulings | A short record stating that 2.1 took no rulings on the owner's behalf, pointing at the design doc's decisions; or the owner waives the criterion for 2.1 | Minutes |

## Owner decisions, 2026-10-17

- **Gap 1:** a new phase, **2.15, size and document the token budget**, added to `ROADMAP.md` and `MILESTONE.md`.
  Milestone 2 stays open until it ships and closes #280.
- **Gap 2:** closed in this change by `docs/plans/2026-09-21-phase-2.1-rulings.md`. It records that 2.1 kept its
  decisions in the design doc and the close-out commit, and does not reconstruct a list after the fact.

Re-run `audit-milestone` after 2.15 merges.
