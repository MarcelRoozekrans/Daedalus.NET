# Milestone 2: Software Manufacturing

**Status:** active
**Started:** 2026-09-21

> Written 2026-09-24, after phase 2.4 closed. Milestone 2 had a goal in `ROADMAP.md` but no
> definition of done; the criteria below were reconstructed from that goal and the phase list and
> **confirmed by the owner on 2026-09-24**. Milestone 1's file is archived at
> `docs/planning/archive/MILESTONE-1.md`.

## Goal

Turn Daedalus from an agent that answers into a system that *builds*: a durable, resumable
pipeline that takes a unit of work from intent to reviewed pull request, staffed by a squad of
role-scoped agents following an explicit written process.

The milestone keeps the Ralph loop's instinct, that work repeats until it converges, and replaces
its substrate with three things: durable state, distinct roles and review gates. Ralph is retired
only once something demonstrably better runs in its place.

## Definition of Done

- [ ] **All planned phases complete.** Phases 2.1 through 2.12 are merged.
- [x] **One manufacture run proven live from intent to reviewed pull request.** It is started
      through `POST /api/workflow-runs`, against a repository the owner chooses. Every step is
      recorded in the run's event log, not asserted:
      - `implement` writes a real change.
      - The review lenses run, with a non-empty `checked[]`.
      - `retrospect` proposes or declines.
      - The human gate parks the run, and it is resumed over HTTP.
      - `publish` opens a real pull request.

      Phases 2.2 to 2.4 each proved part of this and none proved the whole. The criterion exists
      because "reached `Succeeded`" has twice not meant "did the work".

      **Met 2026-10-01, phase 2.5 B17.** Run `dbf7b675` on `daedalus-sandbox` went through every step
      above and opened sandbox PR #7. One caveat: the first `publish` failed on a 403, because the
      token lacked write access. An admin then re-ran only `publish` through the retry that phase 2.5
      Part C added, so no agent node ran twice. The event log records both: `Failed` at seq 6 and
      `Retried` at seq 7, naming the admin. See `docs/plans/2026-09-24-phase-2.5-rulings.md`.
- [x] **Ralph retired only after that run.** Phase 2.8 deletes the loop only once the live run
      above exists, so the retirement proves the replacement works.

      **Met 2026-10-10, phase 2.8.** The loop, its pipeline and the Console host are deleted (PR #329). A board
      task now starts a manufacture run: run `b6fd3a6b` went from task `INV-1` through the gate to `Succeeded`
      and opened daedalus-sandbox#14, the task read In Progress, then Awaiting Approval, then Completed, and
      `/costs` counted the run's node usage exactly once. See `docs/plans/2026-10-10-phase-2.8-rulings.md`.
- [x] **Agent output cannot execute on the host.** A file an agent writes during a run, including
      MSBuild project, props and targets files, is only ever evaluated inside that run's sandbox
      (phase 2.6), never on the API host. Added 2026-09-25 after the MSBuild spike proved the risk.

      **Met 2026-10-04, phase 2.6 B10.** Two tests plant an MSBuild target that writes a marker file and
      assert it exists only inside the container: Thalos A12,
      `An_agent_written_build_target_runs_only_inside_the_container`, and Daedalus B8,
      `An_agent_build_target_never_runs_on_the_api_host`. Run `ac684402` on `daedalus-sandbox` edited a
      `.csproj` and tested it in its sandbox, which was gone about 33 s after the run parked at the
      gate. It opened sandbox PR #8. The earlier run `f6ee2bc2` failed because the agent misread its run
      mode, which process v8 now states. See `docs/plans/2026-10-04-phase-2.6-rulings.md`.
- [ ] **A run is traceable.** A manufacture run emits spans, per phase 2.9, and a failed run can be
      diagnosed from them and the event log without reading agent transcripts.
- [ ] **Spend is visible and bounded.** Agent-turn token usage is aggregated into cost analytics.
      The token budget either caps spend during a turn or is documented as the post-turn check it
      is, sized from measurements rather than picked.
- [ ] **All tests passing.** Unit, Unit.Application, Unit.Domain, Unit.Infrastructure and
      Integration all pass. Playwright.Api and Playwright.Browser pass for any phase that touches
      UI, including 2.12's manufacturing console.
- [ ] **Documentation complete.** Every phase has a design doc, a plan and a rulings record in
      `docs/plans/`.

## Phases

| # | Phase | Status |
|---|---|---|
| 2.1 | Git write tooling | complete (2026-09-21) |
| 2.2 | Durable workflow engine | complete (2026-09-22) |
| 2.3 | The manufacturing squad | complete (2026-09-23; #274) |
| 2.4 | The process as skills | complete (2026-09-24; #276) |
| 2.5 | Run write authority, standing instructions in the target workspace, prompt caching | complete (2026-10-01; #315) |
| 2.6 | Sandboxed run pods | complete (2026-10-04; #320) |
| 2.7 | Issues as a first-class output | complete (2026-10-09; #323) |
| 2.8 | Ralph retirement | complete (2026-10-10; #329) |
| 2.9 | Observability | pending |
| 2.10 | Per-turn tool selection with ZeroAlloc.Jev | pending |
| 2.11 | The scout HTTP path | pending |
| 2.12 | Manufacturing console | pending |

Full phase text, including what each completed phase actually proved and carried forward, is in
`docs/planning/ROADMAP.md`.

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
