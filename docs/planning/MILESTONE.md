# Milestone 2: Software Manufacturing

**Status:** active
**Started:** 2026-09-21

> Written 2026-09-24, after phase 2.4 closed. Milestone 2 was opened with a goal in `ROADMAP.md`
> but no definition of done. The criteria below are reconstructed from that goal and the phase
> list, and they are for the owner to confirm or correct before `audit-milestone` ever reads
> them. Milestone 1's file is archived at `docs/planning/archive/MILESTONE-1.md`.

## Goal

Turn Daedalus from an agent that answers into a system that *builds*: a durable, resumable
pipeline that takes a unit of work from intent to reviewed pull request, staffed by a squad of
role-scoped agents following an explicit written process.

The milestone keeps the Ralph loop's instinct, that work repeats until it converges, and replaces
its substrate with three things: durable state, distinct roles and review gates. Ralph is retired
only once something demonstrably better runs in its place.

## Definition of Done

- [ ] **All planned phases complete.** Phases 2.1 through 2.9 are merged.
- [ ] **One manufacture run proven live from intent to reviewed pull request.** It is started
      through `POST /api/workflow-runs`, against a repository the owner chooses. Every step is
      recorded in the run's event log, not asserted:
      - `implement` writes a real change.
      - The review lenses run, with a non-empty `checked[]`.
      - `retrospect` proposes or declines.
      - The human gate parks the run, and it is resumed over HTTP.
      - `publish` opens a real pull request.

      Phases 2.2 to 2.4 each proved part of this and none proved the whole. The criterion exists
      because "reached `Succeeded`" has twice not meant "did the work".
- [ ] **Ralph retired only after that run.** Phase 2.6 deletes the loop only once the live run
      above exists, so the retirement proves the replacement works.
- [ ] **A run is traceable.** A manufacture run emits spans, per phase 2.7, and a failed run can be
      diagnosed from them and the event log without reading agent transcripts.
- [ ] **Spend is visible and bounded.** Agent-turn token usage is aggregated into cost analytics.
      The token budget either caps spend during a turn or is documented as the post-turn check it
      is, sized from measurements rather than picked.
- [ ] **All tests passing.** Unit, Unit.Application, Unit.Domain, Unit.Infrastructure and
      Integration all pass. Playwright.Api and Playwright.Browser pass for any phase that touches
      UI, including 2.9's manufacturing console.
- [ ] **Documentation complete.** Every phase has a design doc, a plan and a rulings record in
      `docs/plans/`.

## Phases

| # | Phase | Status |
|---|---|---|
| 2.1 | Git write tooling | complete (2026-09-21) |
| 2.2 | Durable workflow engine | complete (2026-09-22) |
| 2.3 | The manufacturing squad | complete (2026-09-23; #274) |
| 2.4 | The process as skills | complete (2026-09-24; #276) |
| 2.5 | Run write authority, standing instructions in the target workspace, prompt caching | pending |
| 2.6 | Ralph retirement | pending |
| 2.7 | Observability | pending |
| 2.8 | The scout HTTP path | pending |
| 2.9 | Manufacturing console | pending |

Full phase text, including what each completed phase actually proved and carried forward, is in
`docs/planning/ROADMAP.md`.

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
