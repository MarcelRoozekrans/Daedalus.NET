# Milestone 2 Audit, second run: Software Manufacturing

**Date:** 2026-10-17
**Audited at:** `main` `cd466736` (#396)
**Definition of done:** `docs/planning/MILESTONE.md`, confirmed by the owner on 2026-09-24
**Earlier run:** `docs/superpowers/reviews/2026-10-17-milestone-2-audit.md`, FAIL on two gaps
**Verdict:** PASS. Every criterion is met. The two gaps from the first run are closed.

## Criteria

| # | Criterion | Result | Evidence |
|---|---|---|---|
| 1 | All planned phases complete | Met | `ROADMAP.md` marks 2.1 to 2.15 complete. 2.15, added by the first audit, merged as #396 |
| 2 | One manufacture run proven live, intent to reviewed PR | Met | Already met 2026-10-01: run `dbf7b675`, daedalus-sandbox PR #7. Re-confirmed on 2026-10-17 by run `facc1bc5`, which opened daedalus-sandbox#25 on the shipped settings |
| 3 | Ralph retired only after that run | Met | Already met 2026-10-10: PR #329, run `b6fd3a6b` |
| 4 | Agent output cannot execute on the host | Met | Already met 2026-10-04: run `ac684402` and the two marker-file tests |
| 5 | A run is traceable | Met | Already met 2026-10-11: run `4949a215`, PR #336 |
| 6 | Spend is visible and bounded | **Met** | See "Gap 1, closed" |
| 7 | All tests passing | Met | All seven suites green on `cd466736`; see "Tests" |
| 8 | Documentation complete | **Met** | See "Gap 2, closed" |
| — | Release will tag correctly | Skipped | `CONVENTIONS.md`: `Milestone completion tags a release: no`. release-please owns releases |

## Gap 1, closed: spend is visible and bounded

The criterion: agent-turn token usage is aggregated into cost analytics, and the token budget "either caps spend during
a turn or is documented as the post-turn check it is, sized from measurements rather than picked".

- **It caps during the turn.** Thalos' `TurnBudgetChatClient` checks the turn's running total before every model
  round trip and stops the turn once it reaches the ceiling. That has been true since Thalos 0.11.0. The first audit
  called it a post-turn check; its dated note corrects that.
- **The ceiling was sized from measurements, not picked.**
  - Phase 2.15 gives each workflow agent its own ceiling, `Thalos:Workflow:TurnBudgets`.
  - The rule is the larger of 1.5 × max and 2 × p90, rounded up to 25,000. It was applied to 87 completed turns from
    `daedalus-sandbox` and Daedalus.NET itself, giving 2,900,000 for implementer, reviewer and `Default`.
  - A drift test pins each value to the rule applied to the recorded maxima.
- **It is documented.**
  - README configuration;
  - the development guide's "Token budget" section, with the query, the rule and how to resize;
  - the `appsettings.json` comments in both hosts.
- **It is visible.** Since phase 2.8 a completed node's usage reaches cost analytics. Since 2.15 a failed node's usage
  does too, including a turn stopped by its ceiling, recorded once the run's failure commits. Live check: run
  `376c1492`, stopped at 990,493 tokens against 875,000, counted on `/costs`.
- **Known limits, tracked:**
  - #394: the run page leaves out a failed node's usage.
  - #395: a completed turn whose outcome the dispatcher rejects is uncounted, and a crash in the moment after a
    failure commits loses that node's record.

## Gap 2, closed: documentation complete

Every phase from 2.1 to 2.15 has a design, a plan and a rulings record.
- **2.1** gained `docs/plans/2026-09-21-phase-2.1-rulings.md` (#392).
- **2.15** has `docs/superpowers/specs/2026-10-17-phase-2.15-token-budget-design.md`,
  `docs/superpowers/plans/2026-10-17-phase-2.15-token-budget-plan.md` and `docs/plans/2026-10-17-phase-2.15-rulings.md`.

The deviation the first run noted still stands, and still doesn't count against the criterion: from 2.6 on, designs
and some plans live under `docs/superpowers/`.

## Tests

Run on `main` `cd466736`, 2026-10-17, with `DOTNET_USE_POLLING_FILE_WATCHER=1`. Release build: 0 warnings, 0 errors.

| Suite | Passed | Failed |
|---|---|---|
| Unit | 2005 | 0 |
| Unit.Domain | 356 | 0 |
| Unit.Application | 281 | 0 |
| Unit.Infrastructure | 258 | 0 |
| Integration | 745 | 0 |
| Playwright.Api | 115 | 0 |
| Playwright.Browser | 219 | 0 |

## Warnings (not counted toward the verdict)

- **No pre-push review reports in this milestone.** The newest `docs/pre-push-review-*.md` is from 2026-08-21.
  Phases 2.2 onwards used a per-task reviewer plus a whole-branch review instead. Phase 2.15's whole-branch review
  ran on the most capable model, with three scoped fix rounds.
- **No full live UI sweep since 2.14.** The last one is `docs/regression-report-2026-10-16-1745.md`. Since then:
  - #391 changed the model behind PRD generation and brainstorm.
  - #396 changed only backend budgets and costs, and was proven live end to end by run `facc1bc5`.
  - PRD generation and brainstorm have still not been run against Anthropic since #391.

## Next

`complete-milestone`. Releases are owned by release-please, so completing the milestone creates no tag. The release
still waits on its manual dispatch, per STATE.
