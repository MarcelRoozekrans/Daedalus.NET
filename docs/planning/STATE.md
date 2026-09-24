# Session State

**Last session:** 2026-09-24

## Current Position - phase 2.4 complete, PR #276 open; phase 2.5 next

**Milestone 2.** Phase 2.4 - the process as skills - is **complete (2026-09-24)**. Branch
`feat/phase-2.4-process-as-skills` is pushed and open as **PR #276** against `main`. Every ruling, the
live proof, the final review and the carried-forward list are in
`docs/plans/2026-09-23-phase-2.4-rulings.md`.

- Suites: Unit 397, Unit.Application 425, Unit.Domain 356, Unit.Infrastructure 142, Integration 581.
- The final review was clean after one fix wave. The Critical it found, any node planting the
  standing-instructions proposal, is fixed: now only retrospect can author it.
- The live proof stopped at `implement`. Review, retrospect and the gate are unreachable until a run
  can write.
- **Roadmap renumbered.** A new **phase 2.5 - run write authority** was inserted, so Ralph retirement
  is now **2.6**. Older text in this file that says "phase 2.5 deletes Ralph" means 2.6.

## Recommended Next Step

1. Merge PR #276, after CI and the owner's review. Then confirm every `fix:` and `feat:` commit
   appears in the release-please PR, because a green workflow does not prove a commit was counted.
2. Start **phase 2.5** with `start-next-phase`. It has no design spec yet, so it routes to a
   brainstorm. It covers the three decisions below and ends with the live proof rerun that 2.4 could
   not complete. Before booting a host for that proof, push the `daily-digest` schedule's
   `NextRunAt` forward, or it fires on startup.

## Decided 2026-09-24

1. **Run writes:** a scoped grant tied to the developer who started the run. Only the `implement`
   node gets `roslyn__apply_*`, with no `git__*` or `repoaction__*`. Each write is logged with the
   starter's id, and publish stays behind the gate.
2. **`AGENT.md`:** resolves against the run's target workspace, which today is the Daedalus repo root.
3. **Prompt caching:** goes into the next Thalos.NET release.
4. **Thalos 0.10.0 release notes:** left as is.

Parked on 2026-09-24: Jev as a conditional tool selector, in `docs/planning/parked-ideas.md`.

---

## History below this line
**Milestone 1 — Hermes-Style Agent Framework: CLOSED (2026-09-21).** All 9 phases complete. See
`docs/planning/MILESTONE.md` for the definition-of-done checklist and its honest scoping, and
`docs/planning/ROADMAP.md` for the "Carried forward from Milestone 1" list of 7 known, deliberately
unfixed items.

**Current milestone:** 2 — Software Manufacturing (**phases 2.1, 2.2 and 2.3 complete; 2.4 next**)

**Phase 2.3 — the manufacturing squad: complete (2026-09-23).** PR #274, 31 commits, plus Thalos.NET
**0.8.0** and **0.9.0** built inside the phase. Unit 328, Integration 540. Full rulings with
cost-if-wrong: `docs/plans/2026-09-23-phase-2.3-rulings.md`.

Two roles: `implementer` on Sonnet 5 and `reviewer` on **Opus 5** — a different model line, not a
different vendor, and the config says so rather than claiming an independence it cannot deliver with
one provider configured. The reviewer holds **no write tool at all**; that rests on absence from its
tool list, not on a policy denial. Per-role memory partitions survive across runs. Review outcomes
must carry evidence: rejecting needs file, line and a failure scenario, approving needs a non-empty
`checked[]`. Three lenses — correctness, falsifiability, mechanism — run sequentially and
short-circuit on the first rejection.

**The central claim was false twice, and neither time was it the design that caught it.** First, the
implementer's memory was retrieved in the reviewer's recall **at similarity 1.00**, because
`WorkflowCaller.Id` carried the run id so both roles shared an owner, and `RememberAsync`'s `shared`
parameter defaults to true. Second, `The_reviewer_is_never_given_the_implementers_summary_or_rationale`
was green from the day it was written **because nothing travelled** — Thalos renders the whole
variable bag and the projection sat downstream of it. Fixed by moving the projection between the
store and the dispatcher, so the renderer receives a bag that never held those keys: absence, not
filtering. The final review verified it by removing the decorator and watching two tests go red.

**Not proved end to end.** Anthropic API credits ran out after one node completed a real turn —
process v3, `implement` as `implementer` on `claude-sonnet-5`, reporting `blocked` honestly, carrying
`squad_mode` and `recall_tier` into `workflow_run_event`. **Never observed live:** any reviewer turn,
any lens pass, any `checked[]`, `files_touched` reaching a reviewer, the gate, or squad-disabled mode
in the event log. **Process version 4 has never been executed at all.**

**Carried forward from 2.3:**

1. `work_intent` has no producer — nothing in `src` calls `StartAsync`. It is one of only two things
   the reviewer is ever given, so in production it can receive only the other.
2. `DetachedRuns:MaxTotalTokens` is 150000 in both hosts; one measured implement turn used **358703
   input tokens**. Deliberately not raised: the sizing is unresolved and raising it without
   understanding the bloat raises the ceiling on waste. The 32 roslyn tool schemas, the skill
   catalogue and the variable block all land in that prompt.
3. `roslyn__apply_code_action` applies a refactoring Roslyn already offers at a position and defaults
   to preview. It is not arbitrary editing, and the §4.1 decision was taken on a broader description
   than the tool supports.
4. Agent turn usage is recorded in `AgentSessions`/`AgentMessages` but never aggregated into cost
   analytics, which reads `TaskExecutions` only — and phase 2.5 deletes Ralph, the only writer of
   those rows. Parked as a phase.
5. A second chat provider would make the different-family reviewer literally true. Parked, with the
   open design question recorded: `IChatClientProvider` resolves one provider per host, not per agent.
6. Commit hygiene checks here cover nested parens and session URLs but **not subject length**; one
   116-character subject failed commitlint on this phase's own PR. `.commitlintrc.yml` caps headers
   at 100.

**Phase 2.1 — git write tooling: complete (2026-09-21).** Branch `feat/phase-2.1-git-tooling`, 6
commits, PR opened against `main`. The roadmap described this phase as building branch, commit, push
and pull-request tools from nothing; most of that already existed. What actually happened: the
capability split across the reusable-framework line recorded on 2026-09-21 — generic git write
actions (`Thalos.Git.GitActionTools`: `git__create_branch`, `git__commit`, `git__push`,
`git__open_pull_request`) shipped in **Thalos.NET 0.6.0**; hosting glue stayed in Daedalus
(`ThalosPullRequestPublisher` implementing Thalos's `IPullRequestPublisher` over the existing
dispatcher, `git` local-tool-source registration, `developer` policy binding in both
`Daedalus.Api/appsettings.json` and `Daedalus.Cli/appsettings.json`). The other real work was
consolidating Daedalus's own GitHub client onto one `GitHubApi` in
`Daedalus.Infrastructure.Services.GitHub`, removing a circular-reference hazard between Agents and
Infrastructure along the way.

**Suites, all green, no pre-existing failure changed:** Domain 338/338, Unit 212/212, Application
409/409, Infrastructure 133/133 — unit total 1092/1092 — Integration 508/508 on the first run (no
crash this time), Playwright.Api 126/126. `Playwright.Browser` was not run — about 17 minutes and
this branch does not touch its fixtures, per standing guidance.

**Phase goal confirmed live, not asserted:** all four `git__*` tools enumerated straight from the
shipped `Thalos.NET.Git` 0.6.0 XML doc comments; both `Daedalus.Api` and `Daedalus.Cli`
`appsettings.json` bind `{ "Pattern": "git__*", "Policy": "developer" }`; and
`RepoToolBoundaryTests` run against the real composed `Daedalus.Api` host (9/9 passing) proves
`git__*` is exposed under its own tool source, every `git__*` tool resolves to the `developer`
policy, and the configured detached-run principal (`DetachedRuns:Roles = ["reader"]`) fails that
policy — a scheduled run cannot branch, commit, push, or open a pull request no matter what its
agent definition lists.

**Not proved end to end against a live remote, by design.** Creating a branch, pushing, and opening
a real pull request is outward-facing and irreversible — it needs a human decision about which
repository and what artefacts are acceptable to leave there. Phase 1.9 proved its *read* path
against live GitHub safely, because reading leaves nothing behind. The write proof is ready and
waiting on that decision.

**Carried forward from phase 2.1:**
1. `IRepositoryAuthenticationProvider` was never registered anywhere on `main`, so Azure DevOps
   pull-request creation could never have worked, on any host, ever. It hid behind resolution
   order — `PullRequestFactory` resolves `GitHubPullRequestFactory` first, so it always failed on
   the GitHub parameter before reaching Azure DevOps. Fixed in this phase's task 5.
2. `Daedalus.Console`'s `RalphLoopWorker` does not call the Agents composition root
   (`AddDaedalusAgents`), so agent-registered services — including all `git__*` tools — are absent
   on that host. Console only calls `AddDaedalusMemory`.
3. The Integration suite test-host-crashed twice during this phase under Docker resource
   contention, with zero failures reported both times. A third and this task's own run were clean.
   Resource contention on this machine, not code.
4. Thalos's `scripts/pack-local.ps1` hard-codes `0.3.0-<suffix>` and never calls GitVersion, so
   local dev feeds carry the wrong version. Real releases are unaffected — GitVersion wins in CI.

**Phase 2.2 — durable workflow engine: complete (2026-09-22).** Branch
`feat/phase-2.2-daedalus-wiring`, 11 tasks across two repos, PR opened against `main`. Design:
`docs/plans/2026-09-22-phase-2.2-workflow-engine-design.md`. Full task-by-task ledger, every
ruling and every deferred item: `.superpowers/sdd/2026-09-22-phase-2.2-workflow-engine-plan/progress.md`.

A spike measured the roadmap's named substrate and **all three candidates failed**:
`ZeroAlloc.StateMachine`'s runtime assembly is seven attribute types and zero non-attribute types,
so a process shape can never come from data; `ZeroAlloc.EventSourcing` has no step or branch
vocabulary at all; `ZeroAlloc.Saga` enforces a contiguous linear step order and supports neither
loops nor branching. The Saga ban was lifted on defect grounds and the exclusion now stands on
architectural grounds instead. `RunStep` was cited as precedent but advances strictly forward, so
it is precedent for the linear shape, not for branching.

The engine was therefore written from scratch, as `Thalos.NET.Workflow` plus
`Thalos.NET.Workflow.Orm`, on **`ZeroAlloc.ORM` and `ZeroAlloc.Outbox.Orm` rather than EF Core** —
which makes 2.2 the pilot for Milestone 3's data-layer migration and keeps the engine EF-free so it
can live in Thalos at all. **Part A** (Tasks 1–8) shipped that engine and released it as
**Thalos.NET 0.7.0**: graph model, loader, validator (every rule fails the *file*, never a paid
run — unknown references, unreachable nodes, a cap with no exit, a gate that also declares
`branch`), the `IWorkflowStore`/`IWorkflowDispatcher`/`IWorkflowReferenceResolver` abstractions,
transactional-outbox dispatch with `xmin` concurrency, a stranded-run reconciler, and hot-reload
with content-hash-enforced immutability (a version's shape cannot change once synced). The
whole-branch review before merge found one Critical of its own kind — `StartAsync` never enqueued
the start node's own dispatch, so a run created through the shipped public API sat at `Running`
forever — fixed before the merge, the third instance this phase of "a carrier built correctly with
nothing consuming it." **Part B** (Tasks 9–11) wired it into Daedalus: `WorkflowCaller`, a
single-role (`"workflow"`) `ISecurityContext` a run's agent turns execute as; the resume/cancel REST
boundary (`WorkflowRunGateway`, the `WorkflowResume` policy — `developer`/`admin` only, and a
workflow-run agent cannot reach it because it is never registered as a tool, not because a policy
denies it); a `BudgetedSubagentRunner` decorator closing a per-turn spend gap the factory pattern
left open; and `processes/manufacture.yaml`, the first real process file.

**The security property of the phase, demonstrated failing then fixed, same as `git__*` in 2.1:**
an agent must not be able to resume its own approval gate. Resume is bound to `developer`/`admin`
and is deliberately not registered as an agent tool — Task 10's review traced this precisely and
caught a comment crediting the *wrong* mechanism (the `WorkflowResume` policy, which a Thalos
`ISecurityContext` never even reaches) for a guarantee the *tool-source absence* actually provides.

**Task 11 proved the whole thing end to end against the real `AppHost`, restart included — the
proof this phase exists to produce.** `processes/manufacture.yaml` transcribes
`subagent-driven-development`'s control flow (implement → review, `maxVisits: 5` /
`onExceeded: adjudicate`, a `human_approval` gate, publish) using agents and skills that
actually exist. Three new skills — `manufacture-implement/-review/-publish` — were authored
for this process, since the design doc's aspirational skill-corpus port has not happened yet.

**Which version the proof ran, stated before anything is claimed for it.** The live runs below
executed **process version 1**, in which `publish` named the `writer` agent and declared no
`outcomes`. That is what the restart proof and the cap/loop-back demonstration exercised: a
`publish` turn running on `writer`'s own configured instructions alone, completing on whatever
that turn produced, with no outcome tool call required. `writer` could not load the
`manufacture-publish` skill's body, because `BuildTaskText` only ever names the skill and
nothing loads its content without `skills__*`, which `writer`'s `Tools` list does not carry.
That defect was found after the runs — commit order shows it: `f62514f` is the restart proof,
`2ecbea7` found the skill-loading failure, `473e16f` bumped the file to **version 2**, which is
what is merged. In v2 all three task nodes run as `Daedalus Architect`, the only configured
agent with `skills__*` at all, and `publish` declares `outcomes: [published]`, reported through
the same structural tool-call mechanism `review` uses, so completing it requires an explicit
call rather than accepting any turn output. **v2 has never been executed by a live run.** It is
proven to load, validate and activate by `ProcessDefinitionSyncEndToEndTests`, which boots a
real host against the merged file — that is the whole of the evidence for v2. The engine
mechanics the runs demonstrate — sequence, branch, gate, kill, restart, resume, loop-back and
cap — do not depend on which agent one node names or whether it declares outcomes, so those
claims stand as written.

**The run.** Started via a throwaway console harness calling `Thalos.Workflow.Orm.OrmWorkflowStore.StartAsync`
directly (no "start a run" surface exists in Daedalus yet — see carried-forward below), the run
was driven to the gate, the **whole AppHost process tree was killed**, the parked run was confirmed to
survive as a bare Postgres row with the host completely down (no process, no thread, no timer),
the host was restarted, and the run was resumed through the real `POST
/api/workflow-runs/{id}/resume` against a real Keycloak-issued `admin` token — completing to
`Succeeded`. A separate run independently exercised the loop-back/cap/`onExceeded` primitive live:
five real `rejected` outcomes, then a real redirect to `adjudicate` — a plain `terminal:
failed`, not a human escalation. A faithful transcription of `subagent-driven-development`'s
own breaker would make this a second gate, which this phase does not build — named in the
YAML file's own comment rather than left implicit. The resume boundary's deny
paths were reconfirmed live on the restarted host too: no bearer token → 401; an authenticated
token with no `developer`/`admin` role → 403.

**Honest limit, observed rather than argued.** Reaching `Succeeded` proves that every node
*declaring* `outcomes` called the outcome tool its schema demanded — in version 1 that is
`review`, not `publish`, which declared none and so completed on turn output alone. It does not
prove the work was real, and this run demonstrates
that gap rather than hiding it: in the successful run, `review`'s `approved` outcome almost
certainly came from the skill's documented fallback ("no prior note visible → approve") rather
than an actual reading of `implement`'s output, because every `AgentMemories` row observed in this
environment — including days-old digest memories — stayed `IndexPending = true`. Semantic recall
had nothing indexed to search within the run's own lifetime. This is Task 10's
"`Succeeded`-without-the-work" finding, observed directly rather than theorised.

**Constraint upheld, not routed around — and name which mechanism upholds which half.** The
policy half is real and verified independently of any run: `appsettings.json`'s `ToolPolicies`
binds `git__*` and `repoaction__*` to `developer`, asserted by
`ApiThalosConfigurationTests.Appsettings_binds_anthropic_defaults_tool_policies_and_sentinel_actions`,
and a workflow turn's `WorkflowCaller` carries the single role `workflow`, which `DeveloperPolicy`
rejects. In the run that actually happened, `publish` executed as `writer`, whose `Tools` list is
`memory__*` alone — so that turn had no such tool to attempt in the first place, and its never
attempting them is tool absence before it is policy denial. The skills say so rather than trying
and failing, which is prompt guidance, not a control. No branch was pushed and no pull request was opened against a real
repository for the workflow's own (test) content; that stays a deliberate, human-triggered step,
exactly as phase 2.1 left its own live-remote proof "ready and waiting."

**Carried forward from phase 2.2** (fuller detail and rulings in `progress.md`, linked above):
1. **The `AGENTS.md` self-improvement loop must move before phase 2.5 deletes it.**
   `RalphPromptTemplateBuilder` instructs agents to "update or create `AGENT.md` … only build/run/test
   instructions" — the standing-instructions loop the Copilot port identifies as compounding, and it
   lives only in Ralph today. Phase 2.5 deletes Ralph. This has to land in the workflow engine (or a
   skill it runs) before that happens, or the capability is lost, not retired.
2. **No "start a workflow run" surface exists in Daedalus.** Tasks 9/10 built resume and cancel only;
   Task 11's own proof had to call `Thalos.Workflow.Orm.OrmWorkflowStore.StartAsync` directly from a
   throwaway console harness because nothing else does. Phase 2.4 (or a dedicated endpoint) needs to
   supply a real trigger.
3. **Cross-node memory handoff does not work at a live run's latency.** `manufacture-implement` and
   `manufacture-review` were designed to hand off through `memory__remember`/`memory__recall`, scoped
   correctly by the run's own `WorkflowCaller.Id` — but every memory row observed in this environment
   stayed `IndexPending = true` well past the run's own lifetime, so semantic recall found nothing to
   search. A process wanting reliable node-to-node handoff needs a different channel; `NodeResult.Variables`
   exists in the store but nothing currently wires it into a later node's task text (a gap Task 5's
   ledger already named and this phase did not close).
4. **`SubagentBudgetExceeded` is easy to hit with a tool-heavy agent.** `Daedalus Architect`'s full
   toolset (`roslyn__*`, `daedalus__*`, `memory__*`, `skills__*`, `context7__*`, `repoaction__*`) plus
   open-ended exploration instructions exhausted the 150,000-token detached-run budget on the very
   first attempt at `manufacture-implement`. Fixed for this process by hard-capping each skill to one
   or two tool calls; the underlying mismatch between that budget and an Architect-class agent's
   toolset remains for any future process node that is less disciplined about it.
5. **Multi-instance duplicate dispatch remains open.** `FetchPendingAsync` has no
   `FOR UPDATE SKIP LOCKED`, so two hosts polling the same outbox table both fetch and both dispatch
   the same row — the `xmin` check means only one transition commits, but both agent turns run and
   both spend. `Daedalus.Cli` was disabled as a workflow host in Task 9 specifically to avoid this;
   the gap reopens the moment the API host itself is scaled past one replica.
6. **The publish node cannot open a real pull request, by design, for now.** `git__*` and
   `repoaction__*` are bound to the `developer` policy and denied to the `workflow` role — deliberate,
   not a gap to close casually. Task 10 recorded **Option C** (host code calls `IPullRequestPublisher`
   directly, after the graph and a human have already decided, removing the model from the trust path
   entirely) as the preferred fix for a later phase; it also closes the `Succeeded`-without-the-work
   gap, since host code would return a real result the graph could branch on. Not built now — it needs
   a new node kind in Thalos, a bigger change than this phase's scope.
7. **A real, unfixed Thalos.NET defect: `ProcessDefinitionSync` has no resilience to the *store*
   failing, only to a *bad document*.** `ProcessDefinitionSync.SyncAsync` does degrade gracefully
   per document exactly as its own remarks describe — a document that fails to load or validate is
   reported and skipped, and the previously-active version keeps serving. What it does not handle is
   `IProcessDefinitionStore.UpsertAndActivateAsync` itself throwing: `OrmProcessDefinitionStore` has
   no `catch` anywhere, so a raw `NpgsqlException` — a missing table, or in production a transient
   connection blip at exactly the wrong moment — escapes `SyncAsync`'s `Result` contract entirely and
   propagates out of `ProcessDefinitionSyncHostedService.StartAsync`, which nothing else catches
   either. Unlike node dispatch, which gets the outbox's retry-with-backoff for exactly this class of
   fault, this call runs once at host boot with no retry of its own — so a transient Postgres problem
   at exactly the wrong second takes the whole host down instead of degrading. This task exposed the
   failure mode (see the phase 2.2 row above) but the fix that actually matters is in Thalos, not in
   Daedalus's tests: `SyncAsync` should catch a per-document store exception the same way it already
   catches a per-document validation failure, and let dispatch's own retry machinery handle a store
   that is down entirely. Worked around here, for the four hosts this task's own change newly
   exposed to it, by disabling `Thalos:Workflow:Enabled` — matching `ApiWebApplicationFactory`'s
   existing, documented pattern — which sidesteps the defect rather than fixing it.
8. **`Daedalus.Api.csproj` had a `processes/` folder wired to nothing.** `Thalos:Workflow:ProcessesRoot`
   could never have resolved a real file, on any host, ever, until this task added the same
   `CopyToOutputDirectory` `Content` item `skills/**/*.SKILL.md` already had. Same shape as phase 2.1's
   `IRepositoryAuthenticationProvider` finding: declared, configured, never actually wired.
   Fixing it with no guard would have been the fourth time in Part B a correct fix shipped
   with nothing to catch a regression, so `ProcessDefinitionSyncEndToEndTests` (Integration)
   now runs the real migrations against a throwaway database, boots a real host with the
   workflow engine left **enabled**, and asserts `manufacture` v1 activates in
   `process_definition` — the one test in the suite that exercises this path end to end,
   the same role `SkillsStartupTests` already plays for `skills/`.
9. Still open from the design doc's own carried-forward list, untouched by this phase: `AGENTS.md`
   (the cross-tool convention, distinct from item 1 above) is never probed by
   `FileSystemWorkspaceContextProvider`; 8 of 14 base skills are multi-file against a single-body
   `Skill` model; `Thalos.NET.Anthropic` is the only chat provider, so `models`/`quorum` on a node
   parse but do nothing; and the squad roster (phase 2.3) still needs the same git-to-Postgres sync
   skills already have.

**Phase 2.3 — the manufacturing squad: in progress** on branch
`feat/phase-2.3-manufacturing-squad`. Design:
`docs/plans/2026-09-23-phase-2.3-manufacturing-squad-design.md`. Two known limits are recorded here
because they are the ones a later phase has to act on, and because a reader of the branch should not
have to reconstruct them:

1. **`DetachedRuns:MaxTotalTokens` is not sized for a manufacturing implement turn.** It is `150000`
   on both hosts, and one measured `implement` turn used **358703 input tokens**. Design §8 lists the
   budget decorator as a cost control; on that measurement it is not one for this node, it is a
   guaranteed `SubagentBudgetExceeded` on the first turn. The number is deliberately left alone —
   raising it to fit one observation would be picking a value, not sizing one — and the comment above
   the key in `appsettings.json` now says so instead of explaining only the scout's sizing. This is
   the same mismatch phase 2.2 carried forward as item 4, now measured rather than anticipated.
2. **`work_intent` has no producer, so the reviewer gets one of its two declared inputs.** Nothing in
   `src` starts a workflow run — the only `StartAsync` reference is `DelegatingWorkflowStore`'s
   pass-through — which is phase 2.2 carried-forward item 2. Design §4 says `work_intent` comes from
   the run's opening variables and `skills/manufacture-review/SKILL.md` told the reviewer it receives
   it; in production a review dispatch can only carry `files_touched`. The skill now says to expect
   that and that it changes nothing about never approving on absence. `SquadHandoffEndToEndTests`
   seeds the key by calling `StartAsync` itself, which is why the contract is testable and still has
   no production path.

**Parked ideas:** `docs/planning/parked-ideas.md` — currently one, a customer chatbot product on Rag.NET, deferred as a separate application rather than a Daedalus milestone. The 1.0 tag on Thalos.NET is
deliberately held back until Milestone 2 settles the agent contracts, since 2.2's workflow engine and
2.3's squad roster will likely want changes to `ISubagentRunner`. Decision recorded 2026-09-21 on
ROADMAP 1.8 and issue #233.

**Phase 1.8 — closed 2026-09-21, docs and architecture-diagrams rewrite, no release.** The plan for
this phase called for shipping Thalos.NET 0.6.0; that call was wrong and is cancelled. Thalos.NET
releases via release-please from conventional commits, and since 0.5.1 there are 18 commits — 17
`chore`, 1 `docs`, zero `feat`, zero `fix` — none releasable without forcing a `Release-As` override.
Confirmed with the user: no override, because nothing in the code changed and a minor version would
signal features that do not exist, undercutting the version-as-signal reasoning that deferred 1.0 in
this same phase. `docs/architecture-diagrams.md` was restructured around the system that exists — 21
sections, 26 mermaid blocks, all verified to render — with fabricated content removed (wrong
`IGitRepositoryManager`/`IRepositoryCodeExtractor` signatures, OpenAI/Copilot named as the LLM backend
where it is Anthropic Claude via Thalos.NET, non-persisted ER entities, symbols that do not exist).
Thalos.NET's 11 packages are documented in that repo's README via
[Thalos.NET#138](https://github.com/MarcelRoozekrans/Thalos.NET/pull/138); the docs are on Thalos.NET
`main` and reach the NuGet package page whenever the next real change ships.

**Phase 1.7 — Daedalus ZeroAlloc migration. Complete and merged** via [#257](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/257).

`CSharpFunctionalExtensions` and both `FluentValidation` packages are gone from the solution and guarded
by architecture tests; the hand-rolled CQRS layer is now `ZeroAlloc.Mediator` 5.1.1. All suites green:
unit 1078, Integration 505, Playwright.Api 126, Playwright.Browser 99.

Three test suites were also repaired along the way, in [#255](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/255)
and [#256](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/256). `Playwright.Api` had been providing
**zero coverage** — 0 of 126 passing, dying at fixture setup in 11 seconds — and now runs 126/126 in about
25 seconds. Nobody had noticed because CI excludes both Playwright projects and the Keycloak tests by name.
**That CI exclusion is still in place** — carried forward, not fixed; see ROADMAP's carried-forward list.

[#258](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/258) then lifted the `ZeroAlloc.Saga` ban and
re-justified `ZeroAlloc.Scheduling`'s, after re-measuring the upstream fixes rather than trusting issue
status. Saga was briefly a candidate for phase 2.2's workflow engine; the 2026-09-22 spike ruled it out on
architecture rather than defects. Milestone 3's premise is half retired because `Saga.Orm` and
`Outbox.Orm` ship with zero EF Core — and 2.2 now pilots that ORM path on greenfield tables.
**Branch state:** `docs/phase-1.8-design`, ahead of `main`. Clean tree apart from the two
deliberately-untracked pre-pivot regression files.

## Current Position

Phase 1.9 is done, merged via [#247](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/247) —
16 commits, 44 files.

The scout can now observe a repository: commits on the default branch, merged and open pull
requests, issues opened or closed, and failed CI runs. That closes the gap where phases 1.4, 1.5 and
1.6 had built the channel, the scheduler and the diagnostics page for a digest whose scout could see
none of the four things its prompt asked for.

**A real GitHub read was performed** against a public repository and returned real data. The AppHost
boots and `/health` returns 200.

**Suite: Unit 1038 passing / 0 failed; Integration 492 passed / 9 failed.** The 9 are the known
Keycloak-dependent `AuthenticationFlowTests`.

### The write boundary

Interactive agents may comment, label and close. **The unattended scheduled run may not**, and that
is enforced by authorization rather than by naming:

- Reads live under the existing `daedalus` tool source; writes under a separate `repoaction` source.
- `repoaction__*` is bound to the existing `developer` policy in `Thalos:ToolPolicies`.
- A scheduled run executes as `schedule:daedalus` with roles `["reader"]`, so `DefaultToolAuthorizer`
  denies it **whatever its tool list says**.

This closes the weakness phase 1.6's own final review flagged in its resend endpoint, where the
boundary rested on tool-surface absence alone.

**The load-bearing test reads the shipped configuration.** `DetachedRuns:Roles` is the single line
that could silently delete the boundary; a test resolves it from the built host and asserts
`DeveloperPolicy` denies it. Adding `developer` there fails the build. Do not replace that test with
a hand-built principal — the original version did exactly that, and six other boundary tests stayed
green while the boundary was gone.

### The dependency blocker is fully resolved

`ZeroAlloc.Results` **1.2.2 is on nuget.org stamped `1.2.2.0`**, and Daedalus adopted it via
[#248](https://github.com/MarcelRoozekrans/Daedalus.NET/pull/248). The central pin moved 1.2.1 →
1.2.2 and **all eleven `VersionOverride` lines are gone**. `Daedalus.Api.deps.json` resolves
`ZeroAlloc.Results/1.2.2` with `assemblyVersion=1.2.2.0`.

Root cause upstream was `publish-from-manifest.yml` building without `-p:Version`, so the assembly
took a fallback while pack overrode only `PackageVersion`. **The same defect was present in 23 of 27
repositories in the ZeroAlloc org**; all 23 are fixed and merged, tracked in
[ZeroAlloc-Net/.github#26](https://github.com/ZeroAlloc-Net/.github/issues/26).

## Blockers

1. ~~The Telegram delivery path is unverified end to end.~~ **CLOSED 2026-09-20.** A real digest was
   delivered to a real chat. The full chain ran: sweeper claimed the schedule, the scout swept the
   repository with the phase 1.9 GitHub tools, the writer produced prose, and the outbox dispatched
   `ChannelMessageQueued` to Telegram — five outbox rows, all succeeded, `RetryCount` 0, no dead
   letters, execution `Done` with no `FailedAtStep`.

   **Verifying it found two real bugs, neither findable any other way.** See "What the first real
   digest cost" below.

2. **`ci.yml` excludes `~Playwright`**, so ~99 browser tests and the whole `Playwright.Api` suite
   never run in CI. The `Playwright.Api` fixture bug — 126 of 126 failing in `OneTimeSetUp` on
   `relation "Skills" does not exist` — is therefore invisible there. Carried from 1.4.

3. **`Daedalus.Cli` has an independent boot failure:** `IProjectRepository` is unregistered for
   `WorkspaceOrchestrator`. It reproduces only under `DOTNET_ENVIRONMENT=Development`, not
   `ASPNETCORE_ENVIRONMENT`, because the CLI uses the generic Host builder.

4. `appsettings.Development.json` carries `postgres`/`postgres` while the compose container
   `daedalus_postgres` uses `daedalus`/`daedalus`.

## Environment note that will bite immediately

**A bare `dotnet run --project src/Daedalus.Api` now fails on `column s.Repository does not exist`.**
Phase 1.9 added an `AddScheduledRunRepository` migration, and a bare run does not apply migrations.
Either use the AppHost, which runs them, or apply it by hand first. This is not a defect; it is the
first phase to add a migration that an existing local database will not already have.

## What the first real digest cost

Two defects surfaced the moment the path was exercised for real. Both are fixed and merged.

**The Telegram channel had never worked, since phase 1.4.** A bot token is `<digits>:<secret>`, so
`bot{token}/{method}` parses as an **absolute** uri whose scheme is `bot<digits>` — a scheme may be
letters and digits followed by a colon. `HttpClient.PostAsync`'s string overload applies
`BaseAddress` only to a *relative* string, so the configured `api.telegram.org` was never consulted
and every call threw `The 'bot<digits>' scheme is not supported`. Fixed upstream in
[Thalos.NET#120](https://github.com/MarcelRoozekrans/Thalos.NET/pull/120), released as 0.5.1,
adopted here in #252.

**Why no test caught it:** every token fixture in the Thalos suite was colon-free — `"TOKEN"`,
`"T"`, `"cfg-token"`. Those build a genuinely *relative* uri, so `BaseAddress` applied and the tests
passed. The existing test asserted exactly the right property with a fixture incapable of exposing
the failure. **The shape of the fixture was the bug.**

**The scout's budget and page size had never been reconciled.** `MaxTotalTokens` was 50000 per
subagent run while `MaxItemsPerCategory` was 50, so the scout could pull five categories of fifty
items on top of the `roslyn__*`, `daedalus__*` and `memory__*` schemas it carries before fetching
anything. Fixed in #251: page size 15, budget 150000, `DeadlineSeconds` deliberately unchanged.

**A third gap was found before it could bite:** the AppHost forwarded only the Anthropic key and the
Telegram token, never `GITHUB_TOKEN`, so under Aspire the scout would have reported every category
unreadable. Fixed in #250.

### Operational notes from that run

- **Orphaned `dcp.exe` processes hold ports 17300, 18889 and 18890** after a killed AppHost, and a
  relaunch then fails with `Unable to allocate a network port` **after** printing its dashboard
  banner. Killing containers is not enough; kill `dcp.exe` too and verify the ports are free.
- **The Aspire Postgres is volume-backed**, so execution and outbox rows survive the container being
  recreated. A stale failed row will look exactly like a fresh failure — check `CreatedAt` before
  concluding anything.
- **Changing a schedule's cron does not recompute `NextRunAt`.** `UpdateFromConfig` sets `Cron` and
  the reconciler does not reschedule, so a cron change takes effect only after the schedule fires
  once on its old one. To fire on demand, set `NextRunAt` into the past directly.

## Known Narrowings — state these rather than paper over them

1. **"Cron wrong" is not representable.** An enabled schedule whose cron never fires reads
   `NotYetDue` or `Overdue`; the diagnostics page cannot say "your cron is wrong." So four and a half
   of the five documented death causes are covered, not five.

2. **A schedule that is both overdue and has a failed last run renders `Failed`**, with a past
   `Next run` beside it and no visual cue.

3. **A non-existent schedule id returns `200 OK` with an empty list**, so "no such schedule" and
   "schedule with zero runs" are indistinguishable to a caller. The agent tool's wording covers both
   honestly; the service-level fix needs a `Result`-shaped return and was deferred.

4. **Four ZeroAlloc packages still carry mis-stamped assemblies** — `Specification` 1.1.0, `Flux`
   1.1.1, `Saga` 2.0.0, `EventSourcing` 1.2.0. All are **latent, not broken**: breakage requires the
   assembly version to go *down* between releases, and these are uniformly wrong rather than
   downgrades. Their repos use multi-package release-please configs, so a `.github/` change released
   nothing; each picks up a correct stamp on its next real code change, with the fix already in place.

5. **`global.json` pins SDK `10.0.401` across the ZeroAlloc org** while runners may only have
   `10.0.400`. This is a race with GitHub's runner-image rollout and caused one publish failure that
   had to be rescued by hand. It will keep failing publishes intermittently until the pin is relaxed.

## Open Decisions (user)

1. **`AgentErrorCode` gaining a `None = 0` member.** `Validation` is member 0, so `default(AgentError)`
   is indistinguishable from a real validation failure. Renumbering is impossible — the enum is
   serialized. Phase 1.6 and 1.9 both took the opposite lesson deliberately: `RunVerdict` reserves
   `Unknown = 0` precisely because of this.
2. **The stranded-run reaper.** 1.6 made stranded runs visible, which was its precondition.
3. **Cross-origin schedule name collision** — still unreachable, since `schedule__create` was
   deliberately cut in 1.6 and not added in 1.9.
4. **Deprecate or unlist the confirmed-broken ZeroAlloc versions** — `Results` 1.2.1,
   `Collections` 1.1.4, `Validation` 1.3.0, `Rest` 1.3.0. They cannot be loaded and leaving them
   listed invites someone else into the same afternoon.
5. **Set an explicit `AssemblyVersion` policy** in the ZeroAlloc repos' `Directory.Build.props`.
   Several declare no version property at all, which is why their fallback was MSBuild's `1.0.0`. A
   deliberate `Major.0.0.0` would make an unversioned build harmless rather than hazardous.

## Recommended Next Step

**Superseded 2026-09-24 — see "Recommended Next Step" at the top of this file.** Everything below
is historical.

**Superseded 2026-09-21 — Milestone 1 is closed; phases 1.7, 1.8 and 1.9 are all complete.** The
paragraphs below describing 1.7 as next are historical and left in place rather than deleted; they
record why 1.7 was unblocked at the time. See the top of this file for the current pointer.

**Superseded 2026-09-21 (later the same day) — phase 2.1 is also complete.** The paragraph below
describing it as next is historical and left in place for the same reason. See the top of this file
for the current pointer.

**Superseded 2026-09-22 — phase 2.2 is also complete.** The paragraph below describing it as
"designed, ready to plan" is historical and left in place for the same reason. See the top of this
file for the current pointer, now phase 2.3.

**Phase 2.1 — git write tooling** is next, opening Milestone 2. It is not blocked: phase 1.9 already
built the authorization boundary (`repoaction__*` bound to the `developer` policy, denied to a
scheduled run regardless of its tool list) that 2.1 extends to git writes.

---

*(Historical, from phase 1.7's close)* **Phase 1.7 — Ralph retirement + ZeroAlloc migration** is next in the roadmap, and nothing now
blocks it.

**That gap is now closed.** A real digest reached a real chat on 2026-09-20, which is the first
end-to-end proof of everything built since 1.4. The remaining blockers are all secondary: CI does
not run the Playwright suites, the Cli has a DI gap in Ralph-era code that 1.7 retires, and the
development connection strings disagree with the compose container.

So 1.7 is genuinely next, with nothing blocking it.

## How This Phase Was Executed — worth carrying

Subagent-driven development: 8 tasks, fresh implementer each, independent review after each, plus a
final whole-branch review. **Twenty-two rulings** are preserved in
`docs/plans/2026-09-19-phase-1.9-rulings.md`.

**Six defects in the implementation plan were found downstream rather than by its author** — a test
snippet that would not compile, a single-argument constructor that weakened a production type, a
hand-built principal that left the security boundary unguarded, a truncation test that exercised
only the already-correct category, a fallback test that could not fail, and a target file containing
no logic at all. The pattern is consistent: the plan's test snippets pin something convenient to
write, and that convenience is exactly what stops the test catching what it exists to catch.

**The defect worth remembering came from the whole-branch review.** One task fixed truncation so it
measured the raw page rather than the filtered list, proven with tests that failed first. A later
task wrote a renderer that checked emptiness *before* truncation and discarded the flag, so a full
page filtering to zero printed "No merged pull requests" — a clean bill of health on a category it
had not finished reading. **Both tasks passed their own reviews.** The defect lived only in the seam
between them, which is exactly where a task-scoped diff review is blind by construction.

**A time bomb from phase 1.6 was also found and fixed here.** Two tests seeded rows at a fixed date
with a one-day lookback but never injected a clock, so they inherited the real one and had been
failing permanently since wall-clock time passed the fixture date. A red test on `main` corrupts the
baseline of every future phase.

## Environment

**Docker UP**, `daedalus_postgres` healthy, `traefik` holding 8080. Keycloak is started by the
Aspire AppHost, not standalone.

- **Use the AppHost.** It brings up Postgres, Keycloak and Ollama together, and applies migrations.
  A bare `dotnet run` on the Api needs `ConnectionStrings__daedalus`, has no Keycloak, and now also
  fails on the unapplied `Repository` column.
- Orphaned `dcp.exe` or dashboard processes from a killed AppHost run hold ports. Aspire reuses an
  existing Keycloak container, so a `keycloak-realm.json` change needs `docker rm -f daedalus-realm-*`.
- `PostgresFixture` builds the test schema with `EnsureCreatedAsync`, **not** `MigrateAsync`. Phase
  1.9 added `AddScheduledRunRepositoryMigrationTests` in `tests/Daedalus.Tests.Integration/Migrations/`,
  which does run `MigrateAsync` — follow that pattern for any future migration rather than verifying
  by hand.
- `dotnet` is a Windows process: give it Windows-style paths, never git-bash POSIX paths.
- Long test runs exceed a 2-minute default timeout. `Playwright.Browser` takes ~11 minutes and is
  excluded from CI; it has stalled implementers who waited on a notification mechanism that does not
  fire in this environment.
