# Pre-Push Review — `feature/thalos-memory` (phase 1.2, Memory)

| Metric | Value |
|---|---|
| Review run | 2026-08-17 23:47 → 2026-08-18 00:07 (local) |
| Branch | `feature/thalos-memory` |
| Base branch | `main` |
| Commits reviewed | 29 |
| Files changed | 93 |
| Lines added | 5,594 |
| Lines removed | 1,437 |
| Plan | `docs/plans/2026-08-17-thalos-memory-plan-b.md` (21 tasks, §0.7 amendments) |
| Design | `docs/plans/2026-08-17-thalos-memory-design.md` (§6) |
| **Verdict** | **FAIL** — 1 Blocker, 5 Warnings, 14 Info |

The blocker is a **commit-message** defect, not a code defect: the CI `commitlint` job rejects one
commit on this branch. The branch is unpushed, so it is a five-minute reword. Everything else that
matters — the migration, the authorization surface, the four suites, the removal of the legacy
learnings slice — was re-run or re-read here and holds up.

---

## 1. Regression evidence (independently re-run, not taken on trust)

Every number the coordinator reported was reproduced on this machine against the branch working tree.

| Suite | Command | Result | Matches coordinator |
|---|---|---|---|
| Build | `dotnet build --nologo` | **0 warnings, 0 errors** (17 projects, 6.2 s) | yes |
| Unit (CI filter) | `dotnet test --no-build --filter "FullyQualifiedName!~Playwright&FullyQualifiedName!~Integration"` | **868 passed, 0 failed, 0 skipped** — Domain 255, Application 368, Unit 115, Infrastructure 130 | yes, exactly |
| Integration | `dotnet test tests/Daedalus.Tests.Integration --no-build --filter "Category!=AuthenticationFlow"` | **343 passed, 0 failed, 0 skipped** (44 s, Docker) | yes, exactly |
| Migration + store slice | `--filter "…AddAgentMemoriesMigrationTests\|…PostgresMemoryStoreTests"` | **26 passed, 0 skipped** | n/a (extra check) |
| Playwright Browser | `dotnet test tests/Daedalus.Tests.Playwright.Browser --no-build` | **99 passed, 0 failed, 0 skipped** (5 m 35 s) | yes, exactly (coordinator: 5 m 22 s) |
| EF model drift | `dotnet ef migrations has-pending-model-changes` | **"No changes have been made to the model since the last migration."** | n/a (extra check) |

`Category=AuthenticationFlow` is excluded by design and the reason is written down at
`.github/workflows/ci.yml:114-116` (those tests drive a *running* API at `localhost:8080` through a
Keycloak container; every other integration test brings its own Testcontainers). Accepted.

---

## 2. Blocker

### B1 — CI `commitlint` job fails on commit `6f89b51` — `footer-max-line-length`

`.github/workflows/ci.yml:191-209` runs commitlint over every commit a pull request adds. Run against
this branch with the repo's own `.commitlintrc.yml`:

```
$ commitlint --from main --to HEAD --verbose
✖   footer's lines must not be longer than 100 characters [footer-max-line-length]
✖   found 1 problems, 1 warnings
(exit 1)
```

The offending commit is **`6f89b51` — "fix(memory): normalise tags in the learnings copy; keep Down
runnable; cover the sweep loop"**. Two message lines exceed 100 characters:

| Message line | Length | Text |
|---|---|---|
| 8 | 101 | `Down recreated StructuredLearnings without the Embedding column, so AddSemanticEmbeddings.Down failed` |
| 9 | 102 | `with 42703 after this migration had already dropped every memory; it re-adds the column now, pinned by` |

Why this is not caught by the repo's own relaxation: `.commitlintrc.yml` disables
**`body-max-line-length`** (level `0`) but leaves **`footer-max-line-length`** at
config-conventional's default (level `2`, 100). `conventional-commits-parser` treats only the *first*
paragraph after the header as the body — **every later paragraph is "footer"** — so the repo's
relaxation covers paragraph 1 only. Lines 3 and 4 of the same message are 101 and 103 characters and
pass for exactly that reason; lines 8 and 9 sit in paragraph 2 and do not.

Verified empirically: reflowing lines 8 and 9 to ≤ 100 characters and re-running commitlint on the
same message yields `found 0 problems, 1 warnings` and exit 0. (The remaining
`footer-leading-blank` is level 1 — a warning, non-fatal, and present on most commits in this repo's
history.)

All 29 headers are conventional and ≤ 100 characters (longest is 99: `9f52f8f`). `6f89b51` is the
only commit that fails; the other 28 pass cleanly.

**This blocks the PR mechanically.** The escape hatch (`skip-commitlint` label,
`.github/workflows/ci.yml:192`) exists for pull requests that import history nobody can rewrite —
this branch is unpushed, so rewriting is the right fix.

---

## 3. Warnings

### W2 — §0.7 has no amendment for `97ff2d5`, and now contradicts the code it shipped

`97ff2d5` — "fix(memory): refuse a double memory registration; never truncate mid-surrogate" — is the
only substantive code commit on the branch with **no §0.7 bullet at all**. Its message calls it "two
follow-ups from the tasks 5–10 review", but the §0.7 G3 review bullet
(`docs/plans/2026-08-17-thalos-memory-plan-b.md:145-154`) documents only `8158f1f`.

Worse, the amendment it should have superseded is still standing and is now **wrong**:

> `docs/plans/2026-08-17-thalos-memory-plan-b.md:151` (§0.7, G3 item 6) — "…`AddDaedalusMemory`'s doc
> states it is for hosts that do **not** call `AddDaedalusAgents` (**a double call is harmless but
> contributes nothing**)."

The code now throws:

- `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs:69` — `ThrowIfMemoryAlreadyRegistered(services, nameof(AddDaedalusAgents));`
- `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs:163` — `ThrowIfMemoryAlreadyRegistered(services, nameof(AddDaedalusMemory));`

Secondary, same commit: the `<exception cref="InvalidOperationException">` doc comments on both
methods (`:59` and `:158`) still list only the *old* throw conditions ("An agent id is neither a ULID
nor a GUID…" / "A `Thalos:Memory` value is out of range") and do not mention the new
double-registration throw — even though the `<remarks>` prose two lines below does say "calling both
throws". The XML contract and the prose disagree.

Also unrecorded in §0.7: the surrogate-safe `Truncate` step-back in
`src/Daedalus.Application/Services/LearningMemoryMapping.cs`. The word "surrogate" appears once in the
plan (`:165`) and refers to Postgres `left()` in the migration SQL, not to this.

The §0.7 log is otherwise exemplary — it records steps that were *skipped* and tests that were *not*
run. This is the one gap, and it is a gap that actively misleads.

### W3 — Embedding model name hard-coded in two hosts, decoupled from the configured dimension

- `src/Daedalus.Api/Program.cs:63` — `new OllamaSharp.OllamaApiClient(new Uri(ollamaConnectionString), "nomic-embed-text")`
- `src/Daedalus.Console/Program.cs:57` — same literal

The dimension that must agree with that model is configuration
(`Thalos:Memory:VectorDimensions: 768`, both `appsettings.json`). `ValidateMemoryConfig` checks
`VectorDimensions > 0` but cannot check *agreement*. Changing the model without the dimension (or the
reverse) produces a mismatch whose only symptom is index writes failing and rows staying
`IndexPending` forever — precisely the silent-degradation mode this branch otherwise works hard to
close off, and one made harder to recover from by the accepted follow-up "no operator affordance for
a full rebuild".

### W4 — The Memories pager does its arithmetic on a `Total` the API documents as over-counted

`src/Daedalus.Web/Pages/Agent.razor:206`, `:211`, `:212`:

```razor
@if (_memoriesTotal > _memories.Count || _memoriesPage > 1)
…@_memoriesPage / @Math.Max(1, (int)Math.Ceiling(_memoriesTotal / (double)MemoriesPageSize))
…Disabled="@(_memoriesPage * MemoriesPageSize >= _memoriesTotal)"
```

The store pages *before* the `MemoryScope.Includes` visibility filter, so `Total` counts rows the
caller cannot see. This is deliberate and documented on the controller
(`src/Daedalus.Api/Controllers/AgentMemoriesController.cs:24-27`) and in §0.7 (G6–G7 item 2) — the
API contract is fine. The **UI** trusts it literally: with any memory pinned to another agent the
page-count label is wrong and "Next" stays enabled into a short or empty page. The E2E fixture seeds
exactly such a pinned row, so the condition is reachable in the shipped test data; it is invisible
today only because 4 rows < one 20-row page.

### W5 — Browser E2E memory seed fails silently — the same failure class this branch was fixed to eliminate

`tests/Daedalus.Tests.Playwright.Browser/Fixtures/E2EServerFixture.cs:489` and `:501`:

```csharp
if (Array.Exists(seeds, s => s.IsFailure))
{
    return;                       // no seed, no message
}
…
catch (DbUpdateException)
{
    // Seed data already exists — safe to ignore
}
```

If an `AgentMemory.Create` ever starts failing (a tightened rule, a changed limit), the scenario
reports "expected 2 items, found 0" instead of naming the cause. Commit `6a0f202b` — "fail the
browser suite loudly" — was written precisely because a broken fixture in this file had been
masquerading as something else since Task 6 (four tests silently `Inconclusive`, exit 0, for eleven
tasks). Leaving another swallow in the same file re-opens a smaller version of that hole. The
`AnyAsync()` guard above already handles the legitimate "already seeded" case, so the
`DbUpdateException` catch has no remaining job.

### W6 — `DeveloperPolicy` instantiated with `new()` on an authorization path

`src/Daedalus.Api/Controllers/AgentMemoriesController.cs:43`:

```csharp
private static readonly DeveloperPolicy Developer = new();
```

`DeveloperPolicy` is also registered with Thalos (`thalos.AddPolicy<DeveloperPolicy>()`), so there are
now two instances of one authorization decision-maker and only one of them is configurable. This is
the **only** `new()`-ed policy in `src/Daedalus.Api` — it is not a repo convention. The policy is
stateless today, so behaviour is correct today; the risk is that the day it takes a dependency (a
role mapping, a claims transformer), the controller's private copy keeps the old behaviour on the
path that gates deleting shared-owner memories, and no test would notice.

---

## 4. Plan adherence

**All 21 tasks accounted for. Nothing missing. No feature smuggled in.**

Tasks 1–19 all have code *and* tests on the branch; task 20 is this review; task 21's step 1
("switch pins to nuget.org, delete `packages-local`") was already satisfied at task 1 — verified:
`nuget.config` has a single `<clear/>`ed nuget.org source, `Directory.Packages.props:44-51` pins all
eight `Thalos.NET*` packages at `0.2.0`, there is no `packages-local/` directory, and no `thalos-local`
string anywhere outside historical prose. Task 21's step 3 (`complete 1.2`, close #228) is
coordinator-owned and correctly left undone.

Deviations checked against §0.7 before being called drift — the ~20 amendment bullets cover
essentially everything, including the ones that look most like scope creep: the `Npgsql 10.0.1→10.0.3`
bump (§0.7 Task 1), `Microsoft.Extensions.TimeProvider.Testing` (Task 13), `InternalsVisibleTo`
(Tasks 2–4, 13), the `RalphRecallConfiguration` move (G3 item 5), the console `appsettings.json` block
(G3 item 3), the `BrowserTestBase`/`E2EServerFixture` failure-mode rewrite (G6–G7 item 1) and the
`IX_AgentMemory_CreatedAt_Id` index (§0.3 + G2 item 2). The six design-doc deviations (uuid vs text
id, two extra indexes, the extra `GET /{id}`, `IKnowledgeBaseToolStatus.LearningsCount` dropped, Ralph
enrichment losing the self-task/project filters, API-host-only schema creation) are all pre-declared
in §0.3, the last two added on this branch — the right place for them.

The only adherence gap is **W2** above. Two changes are unplanned-but-acknowledged and recorded as
Info (§5): the AppHost `WaitForCompletion` fix and the BOM strip.

---

## 5. Migration safety (`20260817194349_AddAgentMemories`)

Highest blast radius on the branch; reviewed line by line and re-run against Testcontainers.

**Clean:**

- **Ordering is correct.** `CreateTable` → five `CreateIndex` → copy SQL → `DROP INDEX IF EXISTS
  "IX_StructuredLearnings_Embedding"` → `DropTable`. EF scaffolded the `DropTable` first; it was moved,
  per the plan. The HNSW index is dropped by raw SQL because it was created by raw SQL and EF does not
  know about it.
- **The copy SQL is entirely constant** — no interpolation, no user input, no injection surface.
- **Tag normalisation mirrors `LearningMemoryMapping`**: `left(lower(btrim(t)), 32)`, blanks dropped,
  de-duplicated keeping first occurrence (`min(ord)`), then capped at 8 — matching what
  `AgentMemory.NormaliseTags` would do on the next edit. This was the correctness fix in `6f89b51`, and
  it matters: without it the first edit of a migrated memory would be rejected by `AgentMemory.Update`.
- **The two remaining unit mismatches are documented in the migration itself** and are genuinely
  harmless: Postgres `left()`/`length()` count code points while `AgentMemory` counts UTF-16 units, so
  a non-BMP text can come out longer than the aggregate's limit — but never split mid-surrogate,
  because `left()` is code-point-based.
- **The `Down` chain stays runnable.** The model lost the `vector(384)` mapping with
  `Pgvector.EntityFrameworkCore`, so the scaffolded `CreateTable` omits `Embedding` — and
  `AddSemanticEmbeddings.Down` would then fail with `42703` *after* this `Down` had already dropped
  every memory. The hand-added `ALTER TABLE "StructuredLearnings" ADD COLUMN IF NOT EXISTS "Embedding"
  vector(384);` (`:162`) closes that, and `AddAgentMemoriesMigrationTests
  .Rolling_back_past_this_migration_runs_the_rest_of_the_down_chain` pins it.
- **`Down`'s destructiveness is stated plainly** in its doc comment — one-way copy, and dropping
  `AgentMemories` destroys every memory written since the upgrade, not just the migrated learnings.
- **No pending model changes**: `dotnet ef migrations has-pending-model-changes` → "No changes have
  been made to the model since the last migration."
- **Re-run here**: `AddAgentMemoriesMigrationTests` (3 facts: full copy incl. every `CASE` arm and the
  tag edge cases, empty-table run, `Down`-chain rollback) plus `PostgresMemoryStoreTests` — 26 passed,
  0 skipped.

**One Info (I4 below):** the `'daedalus'` and `'learning'` literals at `:87`/`:89` are not tied to
`MemoryConfig.SharedOwnerId` / `MemoryKind.Learning`. Hard-coding is the *correct* practice inside a
migration (a migration must not read runtime config), but nothing links the migration's literal to the
default it was written against, so an operator who changed `Thalos:Memory:SharedOwnerId` before
upgrading would silently end up with migrated learnings nothing reads.

---

## 6. Code quality

**Security (OWASP): clean.** All raw SQL is constant. The store's filters go through EF parameters,
including the tag containment (`EF.Property<List<string>>(m, "_tags").Contains(tag)`). No IDOR:
`List`/`Get` apply `MemoryScope.Includes` and answer **404, never 403**, for ids outside the caller's
scope; `Forget` turns on *ownership* (own → allowed; shared owner → `DeveloperPolicy`; anything else →
404 + a foreign-access log line), and the 14 controller integration facts (theory rows expand further)
pin own / shared / foreign / pinned / archived / anonymous. Every endpoint parameter is validated (`MemoryId`/`AgentId`/`MemoryKind`
parse, page and pageSize clamped to `MemoryQuery.MaxPageSize`). No secrets in the diff — the only
credential-shaped strings are Testcontainers literals; the targeted scan for assigned secret values
across `*.json`/`*.cs`/`*.razor`/`*.props`/`*.yml` returned nothing.

**Error handling: clean apart from W5.** `ConfigureAwait(false)` is applied consistently in all new
library code (`PostgresMemoryStore`, `ThalosLearningsMemory`, `ReindexPendingMemoriesHostedService`,
`LearningsService`, `DaedalusLearningsTools`); its absence in `AgentMemoriesController` matches
`AgentSessionsController` and is the repo convention for controllers. `Result` values are checked at
every call site examined. Cancellation tokens propagate end to end, and the reindex loop uses the
clock-aware `Task.Delay(TimeSpan, TimeProvider, CancellationToken)` overload — pinned by a test that
advances `FakeTimeProvider` rather than calling `RunOnceAsync` directly.

**Debug/temporary code: clean.** Zero `Console.WriteLine`, `Debug.WriteLine`, `TODO`, `FIXME`, `HACK`
or `XXX` in added lines across `src/` and `tests/`. No commented-out code, no hard-coded hosts in
production paths.

**Dead code: clean.** The legacy slice was removed in step and leaves no dangling references —
`ILearningsRepository`, `IEmbeddingService`, `OllamaEmbeddingService`, `NoOpEmbeddingService`,
`StructuredLearningEntry`, `LearningsRepository`, `StructuredLearningEntryConfiguration`,
`KnowledgeBaseToolStatus` all gone, together with `Pgvector.EntityFrameworkCore` and every
`UseVector()` call, and the matching `.csproj` / `Directory.Packages.props` entries.

**Naming and doc comments: notably good.** The "why" is captured where it matters — schema ownership,
keyset paging, 404-not-403, `Down` destructiveness.

**Test coverage: clean.** No `Skip=`, no `[Ignore]`, no `Assert.Inconclusive` outside the deliberately
narrowed no-Docker path. New behaviour is covered by Thalos' `IMemoryStore` contract suite plus
Daedalus-specific keyset/tag tests, a real-migration-chain integration test, 14 controller
authorization facts, and unit tests for the DTO mapper, the reindex loop, the learnings adapter and
the mapping. `BrowserTestBase` now `Assert.Fail`s on a recorded startup failure and the fixture probes
`GET /api/agent-memories` beside `/health`, so degraded memory wiring turns the browser suite red
instead of green-with-skips.

**Documentation matches reality.** Grepped the repo for every string this phase removed —
`vector(384)`, `UseVector`, `Pgvector`, `StructuredLearnings`, `IEmbeddingService`,
`ILearningsRepository`, `packages-local`, `thalos-local`, `Thalos.NET* 0.1.1`. Every remaining hit is
either a historical migration (expected), untracked `bin/` output, or prose in `README.md` /
`docs/planning/STATE.md` / `docs/architecture-diagrams.md` that describes the removal — i.e. correct.
No stale hit in live code or configuration. The README endpoint table (`README.md:591-593`) matches
the controller's actual routes and status codes (`AgentMemoriesController.cs:46`, `:110`, `:153`).

---

## 7. Commit hygiene

| Check | Result |
|---|---|
| Conventional headers | 29/29 conventional; all ≤ 100 chars (longest 99, `9f52f8f`) |
| commitlint (repo `.commitlintrc.yml`) | **28/29 pass — `6f89b51` fails (B1)** |
| Secrets | none. Targeted scan for assigned key/token/password/connection-string values across code and config: 0 hits. Pattern hits in the diff are all prose (`ANTHROPIC_API_KEY` named in plan text) or parameter names (`connectionString`) |
| Merge-conflict markers | none |
| Binary / large files | none added; no file over 200 KB |
| Build artifacts | none tracked (`bin/`/`obj/` hits are untracked working-tree output) |
| `packages-local/` reintroduced | no — directory absent, `nuget.config` is nuget.org-only, `.gitignore:89` still excludes `*.nupkg` |
| Commit bodies | substantial and explanatory throughout; `Co-Authored-By` trailer on every commit |

**Nothing unintended staged.** The four pre-existing paths are absent from `git diff main...HEAD` and
remain untracked in the working tree, exactly as required:

- `.claude/settings.local.json` — not in the diff, not in any branch commit
- root `.mcp.json` — not in the diff, not in any branch commit
- `docs/plans/2026-03-01-costs-dashboard-{design,plan}.md` — untracked
- `docs/regression-report-2026-03-01-1800.md` — untracked
- `docs/regression-screenshots/2026-03-01-1800/` — untracked

(Verified two ways: `git log main..HEAD --name-only` matches none of them, and `git status --porcelain`
lists only the four untracked `2026-03-01` paths.)

---

## 8. Info items (no action required before push)

1. **I1 — `ROADMAP.md` / `MILESTONE.md` already say phase 1.2 is `complete`** while task 20 (this
   review) and task 21 step 3 are open and nothing is merged. §0.7 Task 21 explicitly authorises
   writing the rows ahead of the closing steps, and `STATE.md` correctly says "implementation
   complete, closing steps … with the coordinator". Recorded, not drift — **but if this branch is not
   merged, both rows must go back to `active`.**
2. **I2 — `4d96d66` (AppHost `WaitFor` → `WaitForCompletion(migrations)`) is unplanned.** No task
   called for it; the plan declared the symptom out of scope. It is acknowledged in §0.7 (Task 19 item
   4, Task 21), in `STATE.md` and in the README, is a real fix (hosts used to start before the schema
   was applied), is low risk, and sits in its own commit. Clean scope creep.
3. **I3 — `dad91e5` strips BOMs from three source files inside a `docs:` commit.** Trivial, but the
   type does not describe the change.
4. **I4 — migration literals `'daedalus'` / `'learning'`** are not linked to `MemoryConfig.SharedOwnerId`
   / `MemoryKind.Learning` (see §5). Cheap guard: one assertion in `ApiThalosConfigurationTests` that
   `new MemoryConfig().SharedOwnerId == "daedalus"`, commented with the migration name.
5. **I5 — `Agent.razor:437-447` `LoadRecalledAsync`** issues one sequential `GET /{id}` per recalled id
   (up to `Recall:TopK`), runs after *every* turn even when the panel is closed (`:551`), and drops
   non-success results silently, so a 502/401 is indistinguishable from "no longer visible".
6. **I6 — `Agent.razor:400` `LoadMemoriesAsync` has no re-entrancy guard**; rapid Prev/Next can apply
   responses out of order.
7. **I7 — `Agent.razor:226`** duplicates Thalos' `MemoryKind` well-known values as string literals.
   Flagged as intentional in the adjacent comment; it will still drift.
8. **I8 — `AgentMemoriesController.cs:140`, `:193`** log foreign-id access at `Warning`, so an
   authenticated caller can flood warnings by iterating ids. `Information`, or a sampled counter, fits.
9. **I9 — `MemoryDto` ships `OwnerId`, `Source`, `Importance`, `RecallCount`, `LastRecalledAt`** to the
   browser without rendering them. Not a leak (callers only ever receive their own + shared records),
   but it is API surface that now has to stay stable.
10. **I10 — `ReindexPendingMemoriesHostedServiceTests.cs:105-130`** mixes `FakeTimeProvider` with real
    `Task.Delay(50)` and a 20×20×10 ms polling budget. Correct, but a CI-flake candidate.
11. **I11 — behaviour change worth a release note:** the migration does not copy
    `StructuredLearnings.ProjectId` and `search_learnings` lost its `projectId` parameter. Learnings are
    now globally shared under the `daedalus` owner rather than project-scoped. Intended per plan B.
12. **I12 — `STATE.md:6` says "28 commits"**; the branch has 29 (it was written in the 29th).
13. **I13 — the console host's `Thalos:Memory:Reindex` block is inert** (only the API registers the
    sweeper). Documented in `AddDaedalusMemory`'s `<summary>`, so no action — noted for the next reader.
14. **I14 — the six known accepted follow-ups are all recorded**, as required, in
    `docs/planning/STATE.md` "Known follow-ups": reindex log-level ramp (M4), migration command timeout
    (M5), crash-consistency mid-sweep (M8), no operator affordance for a full non-pending rebuild,
    Keycloak `developer` role still missing (it now gates shared-owner memory delete as well as the
    mutating roslyn tools), and the stale local Postgres volume with a collation-version mismatch.
    Verified present and accurately described. No action.

---

## 9. Remediation plan

Ordered by severity. Only item 1 blocks the push.

| # | Severity | Where | Fix | Effort |
|---|---|---|---|---|
| 1 | **Blocker** | commit `6f89b51`, message lines 8–9 | Reword the commit so no line after the *first* body paragraph exceeds 100 chars. Branch is unpushed, so: `git rebase --onto 6f89b51~1 6f89b51 --exec ...` or simply `git rebase -i main`, `reword` that commit, and reflow — e.g. break line 8 after `AddSemanticEmbeddings.Down` and line 9 after `it re-adds the column`. Then re-run `npx --yes --package @commitlint/cli@21.2.1 --package @commitlint/config-conventional@21.2.0 commitlint --from main --to HEAD --verbose` and confirm exit 0. Do **not** reach for the `skip-commitlint` label. | Quick (< 5 min) |
| 2 | Warning | `docs/plans/2026-08-17-thalos-memory-plan-b.md:151` + new §0.7 bullet | Append a §0.7 bullet for `97ff2d5` (double-registration guard + surrogate-safe `Truncate`) and amend G3 item 6: a double call now **throws**, it is not "harmless". | Quick (< 5 min) |
| 3 | Warning | `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs:59`, `:158` | Extend both `<exception cref="InvalidOperationException">` doc comments to name the double-registration case, matching the `<remarks>` prose. | Quick (< 5 min) |
| 4 | Warning | `src/Daedalus.Api/Program.cs:63`, `src/Daedalus.Console/Program.cs:57` | Add `Thalos:Memory:EmbeddingModel` to `MemoryConfig`, read it in both hosts instead of the `"nomic-embed-text"` literal, and assert the known model↔dimension pairing in `ValidateMemoryConfig` (or at minimum in `ApiThalosConfigurationTests`). | Moderate (5–30 min) |
| 5 | Warning | `src/Daedalus.Web/Pages/Agent.razor:206`, `:211`, `:212` | Stop deriving pagination from the over-counted `Total`: either return a post-filter count from the endpoint, or drive "Next" off "the page came back full" and drop the `n / m` label. | Moderate (5–30 min) |
| 6 | Warning | `tests/Daedalus.Tests.Playwright.Browser/Fixtures/E2EServerFixture.cs:489`, `:501` | Throw an `InvalidOperationException` carrying `seeds.First(s => s.IsFailure).Error` instead of `return`, and delete the `catch (DbUpdateException)` — the `AnyAsync()` guard already covers "already seeded". | Quick (< 5 min) |
| 7 | Warning | `src/Daedalus.Api/Controllers/AgentMemoriesController.cs:43` | Inject `DeveloperPolicy` through the constructor instead of `new()`-ing a second static copy of an authorization decision-maker. | Quick (< 5 min) |
| 8 | Info | `ApiThalosConfigurationTests` | Pin `new MemoryConfig().SharedOwnerId == "daedalus"` to the migration literal (I4). | Quick (< 5 min) |

**Minimum to unblock the push: item 1** (and re-run commitlint to confirm). Items 2–3 are documentation
and should ride along in the same pass, since §0.7 is the phase's record of truth and it is currently
wrong. Items 4–7 are legitimate follow-ups that can be scheduled into phase 1.3 if the coordinator
prefers to ship now — none of them is a correctness bug in shipped behaviour, and all four suites are
green.

After remediation, re-run the four suites (the reword in item 1 changes no code, so a build + unit run
suffices unless items 4–7 are also applied).
