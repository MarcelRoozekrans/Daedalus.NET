# Milestone 3 Design: Native AOT

**Date:** 2026-10-17
**Milestone:** 3
**Stage:** milestone (one milestone, multiple phases)

Brainstormed with the owner on 2026-10-17. What the owner decided is marked as theirs. Every other decision is marked
**(my call)**.

**Evidence:** `docs/plans/2026-10-17-native-aot-survey.md`, an AOT compile of every target on `main` `04a67164`. The
machine had no native toolchain and no WebAssembly tools, so it counts warnings only; nothing was linked or run.

## Goal

Daedalus and Thalos.NET ship as Native AOT. The API, the CLI, the migration runner and the sandbox host publish as
native binaries, the Web app publishes with WebAssembly AOT, and every Thalos.NET package is AOT-compatible.

**Why (the owner's reason):** EF Core has no roadmap to Native AOT. If Daedalus is ever to ship AOT, the data layer
must leave EF Core, and every month on it adds code that has to move later. The milestone does that move
deliberately, now.

The faster start-up, smaller memory and smaller images that come with AOT are welcome, and are measured, but they are
not the reason. The AOT-compatible Thalos.NET also opens the framework to consumers that must ship AOT themselves.

## Definition of Done

- [ ] **All planned phases complete.** Phases 3.1 to 3.7 are merged.
- [ ] **All tests passing.** Unit, Unit.Domain, Unit.Application, Unit.Infrastructure, Integration, Playwright.Api
      and Playwright.Browser all pass. Playwright.Api also runs against the **native** API binary, and Playwright.Browser
      against the **WebAssembly AOT** Web build.
- [ ] **Native targets publish clean.**
  - `Daedalus.Api`, `Daedalus.Cli`, `Daedalus.Migrations` and `Daedalus.Sandbox` publish with `PublishAot=true`, in
    CI, with **zero trim or AOT warnings and no suppressions**.
  - Each native binary starts and does its job: the CLI answers a turn, Migrations upgrades a database, the sandbox
    host serves a run, and the API passes the API suite.
- [ ] **Thalos.NET is AOT-compatible.** Every packable Thalos project sets `IsAotCompatible=true` and builds with
      zero trim or AOT warnings, on both target frameworks. It is released as a Thalos version Daedalus consumes.
      `Thalos.NET.Testing` is exempt: it ships only into test projects, never into a published app **(my call)**.
- [ ] **The Web app ships WebAssembly AOT.** `Daedalus.Web` publishes with `RunAOTCompilation=true`, with zero trim
      warnings. The browser suite passes against that build.
- [ ] **EF Core is gone.**
  - `Microsoft.EntityFrameworkCore*` and `Npgsql.EntityFrameworkCore.PostgreSQL` appear nowhere in the dependency tree.
  - An existing database upgrades in place with its data intact, proven on a copy of the dev database: runs, tasks,
    cost records and agent sessions read back unchanged.
- [ ] **The HTTP contract is unchanged.** The 18 controllers became minimal API endpoints. Playwright.Api and the Web
      app pass without changes to what they assert (owner: identical contract).
- [ ] **A live manufacture run succeeds on the native API.** It goes from a board task through the gate to a pull
      request on `daedalus-sandbox`, with its spend on `/costs`.
- [ ] **The gains are measured.** Start-up time, idle and peak working set, and image size are recorded for the API,
      the CLI and the sandbox host, before (JIT) and after (native), in the milestone's rulings. They are recorded,
      not thresholds. **(my call)**
- [ ] **Documentation complete.** Every phase has a design, a plan and a rulings record. The README and the
      development guide describe the native build, its toolchain, and the AOT rules a contributor must follow.

There is no release-tag line: `CONVENTIONS.md` says release-please owns releases.

## Phases

1. **Phase 3.1: AOT toolchain, gates and the native sandbox host.** `Surface: Infra`
   - **Goal:** Native publishing works where it matters, CI blocks new trim and AOT warnings, and the sandbox host
     ships native first.
   - **Toolchain:** clang and zlib on the agent VM and in CI, plus the `wasm-tools` workload.
   - **Baseline:** the JIT "before" measurements are taken here, before 3.2 changes anything, and recorded in 3.1's
     rulings:
     - API: time to healthy, idle and peak working set, image size;
     - CLI: time to answer;
     - sandbox host: time to serve, image size.

     **(my call)**
   - **ORM gap audit:** ZeroAlloc.ORM is checked against every repository and store here, so upstream fixes start
     long before 3.5 needs them. Covered: pgvector, jsonb, the cost-analytics aggregations, optimistic concurrency,
     taking over the EF migration history, typed-id conversion, and the outbox, scheduling and diagnostics stores.
     **(my call)**
   - **CI gate:** an AOT-analysis job per target that records today's warning counts, and fails when a count rises.
   - **Note, 2026-10-17 (owner):** the `aot-analysis` job becomes a required status check on `main` once it is green
     on 3.1's PR. That is a branch-ruleset change only the owner can make. Until then, every milestone 3 PR treats a
     red `aot-analysis` as blocking.
   - **As built, 2026-10-21:** the ratchet fails on a count that rises **or** falls, so `eng/aot/baseline.json`
     always states the truth. The CLI's baseline measures time to ready, not time to answer, which needs model
     credits. Rulings: `docs/plans/2026-10-17-phase-3.1-rulings.md`.
   - **First native target:** fix `Thalos.NET.Sandbox.Host`'s 8 warnings and publish `Daedalus.Sandbox` native.
   - **First runtime evidence:** a native smoke run of each server target as far as it gets, which turns the
     survey's biggest unknown into evidence.
2. **Phase 3.2: Our own reflection out.** `Surface: Refactor`
   - **Goal:** Daedalus' and Thalos' own code is AOT-clean, leaving only the third-party blockers that 3.3 to 3.5
     remove.
   - **What it fixes:**
     - System.Text.Json **source-generated contexts** for every serialised type, Thalos `Workflow.Orm` first, at 36
       warnings. Stored JSON and the wire format stay byte-for-byte identical (owner: source-generated now, the
       `ZeroAlloc.Serialisation` swap later);
     - the reflection-based tool discovery in `LocalToolSource` and `McpToolBuilder`, which becomes a
       source-generated tool registry. This is the one design item in the phase, so 3.2's phase design settles its
       shape **(my call)**;
     - the configuration-binding source generator;
     - `[DynamicallyAccessedMembers]` on DI generics;
     - ZeroAlloc typed-id and outbox reflection;
     - the AI.Sentinel annotation;
     - YamlDotNet's static generator in `Thalos.Workflow` **(my call)**.
   - **ServiceDefaults:** remove the Aspire hosting packages it pulls into the API, 128 warnings.
   - **Carried by the roadmap:** `ZeroAlloc.Inject` for compile-time DI, and `ZeroAlloc.Resilience` for the two
     `AddStandardResilienceHandler` sites. `ZeroAlloc.Serialisation` is **not** in this milestone (owner,
     2026-10-17). It moves to a later phase or milestone of its own.
3. **Phase 3.3: Thalos' own Anthropic and Ollama clients.** `Surface: Backend`
   - **Goal:** The Anthropic SDK and OllamaSharp leave the dependency tree, replaced by source-generated clients in
     Thalos that implement `IChatClient` and `IEmbeddingGenerator` (owner: own client).
   - **Why:** the Anthropic SDK is about 15,000 of the API's 16,725 warnings, from reflection JSON in every model type.
     It is not part of Microsoft.Extensions.AI, which had zero warnings.
   - **What the client must match:**
     - streaming, tools and the required-outcome tool;
     - prompt-cache breakpoints;
     - usage, including the cache-read and cache-write split the turn budget and cost analytics depend on;
     - error mapping.
   - **Unchanged above it:** the Agent Framework, the Thalos runtime, the budget client and the cache translator.
   - **Scope:** every current use of the SDK, in Thalos and in Daedalus' `AgentFactory` (PRD generation and
     brainstorm). 3.3's design inventories which features those uses rely on.
   - **Proof:** recorded-stream tests against real API transcripts first. The live proof follows once Anthropic
     credits are back.
   - **Upstream:** file the issue with Anthropic.
   - **OllamaSharp,** used for embeddings, is replaced the same way **(my call)**.
   - **Note, 2026-10-17 (owner): where the clients live.**
     - The Anthropic client is `ZeroAlloc.Rest.Anthropic`, a ZeroAlloc-Net package with no Microsoft.Extensions.AI
       dependency.
     - `Thalos.NET.Anthropic` keeps only the `IChatClient` adapter and the cache translator, over that client.
     - Ollama follows the same pattern, as `ZeroAlloc.Rest.Ollama`.

     This supersedes "source-generated clients in Thalos" in the goal above.
   - **Note, 2026-10-17 (owner): the Anthropic issue is filed only once it is proven.** That means checking against
     the latest SDK release first, and a native run that reproduces the failure at run time; warnings alone are not
     enough. Phase 3.1 found that Thalos.NET.Anthropic 0.18.0 requires Anthropic >= 12.58.0, which raised the API's
     count from 18238 to 18591; the fix is here, in 3.3.
4. **Phase 3.4: Controllers to minimal APIs.** `Surface: Refactor`
   - **Goal:** The 18 MVC controllers become minimal API endpoints with an identical contract (owner).
   - **What "identical" covers:** routes, verbs, status codes, `ProblemDetails` bodies, authorization policies, rate
     limits, validation and OpenAPI.
   - **Proof:** Playwright.Api, 115 tests, and the Web app pass unchanged.
   - **Out of scope:** known contract bugs such as #366 and #326 stay in their own issues.
5. **Phase 3.5: EF Core to ZeroAlloc.ORM.** `Surface: Data`
   - **Goal:** The data layer runs on ZeroAlloc.ORM over the **existing schema and data** (owner), and EF Core is
     removed.
   - **Migrations:** ZeroAlloc.ORM's `MigrationRunner` takes over from EF's migration history, so existing databases
     upgrade in place.
   - **Order:** repositories and query services move store by store, with `ZeroAlloc.Linq` for query composition.
     *(Corrected 2026-10-21: this said `ZeroAlloc.Specification`, which CLAUDE.md rules out; the owner's decision
     below replaces it.)*
   - **Note, 2026-10-17 (owner): ZeroAlloc.Linq and the ORM gaps.**
     - ZeroAlloc.ORM gains the gaps the phase 3.1 audit confirmed, and **ZeroAlloc.Linq**, compile-time LINQ to SQL.
     - Both are built by the owner's separate ZeroAlloc session, from
       `docs/plans/2026-10-17-zeroalloc-linq-handover.md`, not as a Daedalus phase.
     - Daedalus' queries stay LINQ, translated to SQL at build time.
     - **3.5 depends on both being released.**
   - **Size, from the phase 3.1 audit (2026-10-17):** the largest phase of the milestone. About 117 statements over
     the 22 files that run queries; about 30 change-tracked writes that become explicit; about 20 rehydration
     factories; one baseline migration in place of 26 EF ones; the outbox move with its pending rows and leases; and
     fixture rework off `EnsureCreatedAsync` under the integration suite's parity bar. About 5 statements stay raw
     SQL: `FOR UPDATE` and the upserts. Audit: `docs/plans/2026-10-17-zeroalloc-orm-gap-audit.md`, section 5.2.
   - **What the audit changed:**
     - the gaps are six, not three: schema adoption, arrays, jsonb parameters, unsigned integers for `xmin`,
       `ZeroAlloc.Outbox.Orm`'s missing dashboard store and fixed table name, and a silently skipped 14-digit
       migration version. Arrays block every store that touches `text[]` or `Contains`, and the `Outbox.Orm` gap
       blocks the outbox move; the others have stopgaps;
     - pgvector needs no ORM support: no Daedalus table has a `vector` column, and Rag.NET creates the extension;
     - no Daedalus column is a typed id, so the typed-id converter below has nothing to convert;
     - six `bytea` row versions enforce nothing (#290), which 3.5 settles.
   - **Outbox and companion stores:** the outbox, scheduling and diagnostics stores move from their `.EfCore` packages
     to their ORM counterparts, with in-flight outbox messages and leases carried over. Typed-id persistence gets an
     ORM-side converter; if ZeroAlloc-Net lacks one, it is fixed upstream **(my call)**.
   - **Parity proof:** the integration suite, 745 tests, plus a copy-of-dev-database upgrade test. Tests may change
     their fixtures and seeding off `DbContext`. Their **assertions** stay as they are, and each changed test is
     listed in the rulings **(my call)**.
   - **No dual-ORM end state:** both ORMs may run side by side only within this phase's branch, never on `main`
     between phases **(my call)**.
6. **Phase 3.6: Native API, CLI and Migrations.** `Surface: Infra`
   - **Goal:** The three remaining server targets publish native in CI with zero warnings, ship as images, and pass
     their suites as native binaries.
   - **Native test runs:**
     - Playwright.Api runs against the native API;
     - the CLI runs a turn;
     - Migrations upgrades a database.
   - **Live proof:** one manufacture run on the native API.
   - **Measurements:** before and after.
   - **Carried:** `ZeroAlloc.Collections`, only where those measurements justify it, as the roadmap says.
7. **Phase 3.7: Blazor WebAssembly AOT.** `Surface: Infra`
   - **Goal:** `Daedalus.Web` publishes with WebAssembly AOT, with zero trim warnings, and the browser suite passes
     against it.
   - **What it fixes:** the Web app's own `HttpClient` JSON calls, 41 trim warnings, move to source-generated contexts,
     and Radzen is checked against the policy below.
   - **Measured:** download size and start-up. **(my call)** Surface `Infra`, not `UI`, because nothing visual
     changes.

Order: 3.1 first. 3.2 before 3.3 to 3.5, because it clears the ground they build on. 3.3, 3.4 and 3.5 are
independent of each other. 3.6 needs 3.2 to 3.5. 3.7 needs only 3.1 and 3.2, so it can run any time after them.
**(my call)** 3.5 also waits on ZeroAlloc.Linq and the ORM gaps being released (owner, 2026-10-17).

## Policies that bind every phase

- **Where native applies:**
  - Development, the AppHost, `dotnet test` and the sandbox image's own SDK stay JIT.
  - Native is the CI publish of each target and the images built from it.
  - The trim and AOT analyzers run in every build, so warnings surface early.
  - The sandbox image gets the native host binary in its runtime layer. The image keeps the SDK, which runs need to
    build user code.
  - 3.6's phase design decides how the native API runs beside Postgres and Keycloak for the test suites.

  **(my call)**

- **Blocking dependencies (owner):**
  - A library we own (Thalos, ZeroAlloc-Net, AI.Sentinel, Rag.NET) is fixed upstream.
  - A third-party library gets an upstream issue, and is replaced if an AOT-clean alternative exists.
  - If neither works, the milestone waits on it, tracked as an issue.
  - Nothing is suppressed: an `UnconditionalSuppressMessage` or `<NoWarn>` for a trim or AOT warning counts as a
    workaround.
- **Zero warnings means no suppressions.** A warning is fixed at its source or the target does not ship. A
  suppression is acceptable only where the code is provably safe and the reason is written beside it, for example
  a type preserved by a source generator. Each one is listed in its phase's rulings. **(my call)**
- **Warning-free is not the same as working.** Every native target is proven by running it, not by its build
  output alone.
- **No data loss, no contract change.** The schema, the data and the HTTP contract survive every phase.

## Dependencies on Prior Milestones

- **Milestone 2 complete (2026-10-17).** The manufacture pipeline, the per-role budgets and the cost analytics that
  3.3's usage counts must keep feeding.
- **Thalos.NET:** most of 3.1 to 3.3 lands in Thalos and ships as Thalos releases that Daedalus consumes, as in
  milestone 2.
- **ZeroAlloc.ORM:** must cover what EF Core does for Daedalus today on PostgreSQL. That includes jsonb, the
  cost-analytics aggregations, optimistic concurrency, and taking over an EF migration history. pgvector turned out
  not to be needed (phase 3.1 audit). ZeroAlloc.Linq and the audited gaps must be released before 3.5 starts. Gaps
  are fixed upstream, per the policy. Thalos' `Workflow.Orm`, `Saga.Orm` and `Outbox.Orm` already run on it, which is
  the evidence it can.

## External Constraints

- **Native toolchain:** clang and zlib headers are missing on the agent VM, and the `wasm-tools` workload is not
  installed. 3.1 installs them, which the homelab VM may need its owner for.
- **Anthropic credits:** these are used up as of 2026-10-17. The live proofs in 3.3 and 3.6 need them.
- **Upstream release cadence:** a fix in a library we don't own may take time. The policy says wait and track rather
  than work around it.

## Risk Areas

| Risk | Impact | Mitigation |
|---|---|---|
| A native binary that builds cleanly fails at run time: reflection a library hides, native library loading (LibGit2Sharp), a dynamic code path the analyzer never saw | A target thought done is not | 3.1's native smoke runs. Every target is proven by running its suite natively, not by warnings |
| ZeroAlloc.ORM lacks something EF Core gives Daedalus: pgvector, a cost-analytics query, migration-history takeover | 3.5 stalls | Audit at 3.5's start against every repository, and fix gaps upstream |
| The own Anthropic client drifts from the API: a new content block, a streaming event, cache usage fields | Broken turns, or wrong budgets and costs | Recorded-stream tests against real API transcripts; usage-split parity with today's numbers |
| Blazor WebAssembly AOT makes the download far larger, or Radzen is not trim-safe | Slower first load, or 3.7 blocked | Measure first in 3.7. Radzen goes through the blocking-dependency policy |
| Milestone size: the largest so far | Long-lived branches, drift from `main` | Seven independent phases, each merged on its own. No dual-ORM state on `main` |
| An unmeasured library is not AOT-clean: ModelContextProtocol, Microsoft.Agents.AI, Rag.NET and LibGit2Sharp showed zero warnings, but reachability is unproven | A late blocker | 3.1's whole-program native smoke run of each target |

## Open Questions

None blocking. The milestone's phase specs settle phase-level choices, such as the shape of the Anthropic client's
public API and the order in which the repositories move.
