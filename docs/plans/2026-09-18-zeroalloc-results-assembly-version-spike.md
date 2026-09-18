# Task 1 — De-risk the boot blocker

**Status: DONE.** A viable fix exists, was applied, and was verified end to end: the API host boots
and serves HTTP, and the full test suite is unchanged. The fix was then reverted — this task ships an
answer, not code.

**Headline:** the blocker is *not* a conflict between `ZeroAlloc.Authorization` and `Thalos.NET`.
`ZeroAlloc.Results` **1.2.1 is a broken package**: it ships an assembly stamped `0.1.0.0`, a *downgrade*
from 1.2.0's `1.2.0.0`. Every consumer in the graph asks for a higher version than the file provides, so
the default host binder refuses it. Pinning `ZeroAlloc.Results` to **1.2.0** (identical public API, no
library source change) clears the blocker.

---

## Step 1 — Reproduction and stack trace

Both hosts reproduce, on the same frame.

### `dotnet run --project src/Daedalus.Api --no-build`

```
Unhandled exception. System.IO.FileNotFoundException: Could not load file or assembly
'ZeroAlloc.Results, Version=0.1.4.0, Culture=neutral, PublicKeyToken=null'.
File name: 'ZeroAlloc.Results, Version=0.1.4.0, Culture=neutral, PublicKeyToken=null'
   at Daedalus.Agents.DaedalusAgentsServiceCollectionExtensions.<>c__DisplayClass2_0.<AddDaedalusAgents>b__0(ThalosBuilder thalos) in ...\DaedalusAgentsServiceCollectionExtensions.cs:line 123
   at Daedalus.Agents.DaedalusAgentsServiceCollectionExtensions.<>c__DisplayClass2_0.<AddDaedalusAgents>b__0(ThalosBuilder thalos) in ...\DaedalusAgentsServiceCollectionExtensions.cs:line 123
   at Thalos.ThalosServiceCollectionExtensions.AddThalos(IServiceCollection services, Action`1 configure)
   at Daedalus.Agents.DaedalusAgentsServiceCollectionExtensions.AddDaedalusAgents(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, IEmbeddingGenerator`2 embeddingGenerator) in ...\DaedalusAgentsServiceCollectionExtensions.cs:line 122
   at Program.<Main>$(String[] args) in C:\Projects\Prive\daedalus\src\Daedalus.Api\Program.cs:line 70
```

### `dotnet run --project src/Daedalus.Cli --no-build`

```
Application terminated unexpectedly: System.IO.FileNotFoundException: Could not load file or assembly
'ZeroAlloc.Results, Version=0.1.4.0, Culture=neutral, PublicKeyToken=null'.
   at Daedalus.Agents.DaedalusAgentsServiceCollectionExtensions.<>c__DisplayClass2_0.<AddDaedalusAgents>b__0(ThalosBuilder thalos) in ...\DaedalusAgentsServiceCollectionExtensions.cs:line 123
   ... at Daedalus.Cli.CliHostServices.ConfigureServices(...) in ...\src\Daedalus.Cli\Program.cs:line 73
```

**The frame that triggers the load.** Line 123 is the *opening brace* of the `services.AddThalos(thalos => { … })`
lambda — no statement in the body has run. This is a JIT-time type-load failure on entry to the lambda, not a
failure of any particular call inside it. The trigger is signature resolution, not execution.

**Which assembly asks for 0.1.4.0.** Reading the `AssemblyRef` tables of every DLL in
`src/Daedalus.Api/bin/Debug/net10.0/` shows *three distinct* requested versions of `ZeroAlloc.Results`:

| Requesting assembly | AssemblyRef version |
|---|---|
| `Daedalus.Agents`, `Daedalus.Api`, `Thalos.NET`, `Thalos.NET.Abstractions`, `Thalos.NET.Channels`, `Thalos.NET.Mcp`, `Thalos.NET.Memory`, `Thalos.NET.Memory.RagNet`, `Thalos.NET.Skills` | **0.1.0.0** |
| `ZeroAlloc.Authorization` | **0.1.4.0** |
| `AI.Sentinel`, `Rag.NET.Abstractions` | **1.2.0.0** |

The file actually shipped by `ZeroAlloc.Results` **1.2.1** is `AssemblyVersion 0.1.0.0`. The default binder
takes the TPA candidate by simple name and rejects it when its version is *lower* than requested, so
`Authorization` (0.1.4.0) fails first — and `AI.Sentinel` / `Rag.NET.Abstractions` (1.2.0.0) would have
failed next. Fixing only `Authorization` would have moved the crash, not removed it.

`ZeroAlloc.Authorization`'s entire dependence on `ZeroAlloc.Results` is one type:

```
TYPES:
  ZeroAlloc.Results.UnitResult`1   [ZeroAlloc.Results v0.1.4.0]
```

---

## Step 2 — Why the tests are unaffected

### The deps.json comparison

The two files agree exactly on the entries the brief asked about — this is **not** a packaging difference:

```
===== src/Daedalus.Api/bin/Debug/net10.0/Daedalus.Api.deps.json
  ZeroAlloc.Authorization/2.1.0
    runtime lib/net10.0/ZeroAlloc.Authorization.dll {'assemblyVersion': '9.9.9.0', 'fileVersion': '9.9.9.0'}
    dep ZeroAlloc.Results 1.2.1
  ZeroAlloc.Results/1.2.1
    runtime lib/net10.0/ZeroAlloc.Results.dll {'assemblyVersion': '0.1.0.0', 'fileVersion': '0.1.0.0'}

===== tests/Daedalus.Tests.Integration/bin/Debug/net10.0/Daedalus.Tests.Integration.deps.json
  ZeroAlloc.Authorization/2.1.0
    runtime lib/net10.0/ZeroAlloc.Authorization.dll {'assemblyVersion': '9.9.9.0', 'fileVersion': '9.9.9.0'}
    dep ZeroAlloc.Results 1.2.1
  ZeroAlloc.Results/1.2.1
    runtime lib/net10.0/ZeroAlloc.Results.dll {'assemblyVersion': '0.1.0.0', 'fileVersion': '0.1.0.0'}
```

Two things fall out of that block on their own:

1. NuGet resolved `ZeroAlloc.Results` to **1.2.1** for both, and `ZeroAlloc.Authorization/2.1.0`'s *package*
   dependency is recorded as `ZeroAlloc.Results 1.2.1`. There is no unresolved package conflict anywhere.
2. `assemblyVersion` is **0.1.0.0** for a package numbered **1.2.1**. That is the bug, visible in the very
   field the brief pointed at.

### The mechanism, demonstrated

A two-package, twenty-line throwaway (scratchpad only — no repo files) referencing exactly
`ZeroAlloc.Authorization 2.1.0` + `ZeroAlloc.Results 1.2.1` and doing nothing but
`Assembly.Load("ZeroAlloc.Results, Version=0.1.4.0")`:

**Run as an app (`dotnet App.dll`):**

```
start
A: Assembly.Load(ZeroAlloc.Results, Version=0.1.4.0)
Unhandled exception. System.IO.FileNotFoundException: Could not load file or assembly
'ZeroAlloc.Results, Version=0.1.4.0, Culture=neutral, PublicKeyToken=null'.
```

**The same two calls as xUnit facts (`dotnet test`):**

```
Passed ReproTest.A_Assembly_Load_of_Results_0_1_4_0_under_the_test_host [1 ms]
 resolved -> ZeroAlloc.Results, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null (...\bin\Debug\net10.0\ZeroAlloc.Results.dll)
Passed ReproTest.B_Reflect_over_IAuthorizationPolicy_signatures [6 ms]
 System.Threading.Tasks.ValueTask`1[[ZeroAlloc.Results.UnitResult`1[[ZeroAlloc.Authorization.AuthorizationFailure, ZeroAlloc.Authorization, Version=9.9.9.0, ...]], ZeroAlloc.Results, Version=0.1.0.0, ...]] EvaluateAsync
```

Same code, same package graph, same deps.json — one host binds, the other refuses.

**Why.** The VSTest host installs its own resolver. `Microsoft.VisualStudio.TestPlatform.Common.dll` in every
test output directory contains
`Microsoft.VisualStudio.TestPlatform.Common.Utilities.AssemblyResolver` (`SetupAssemblyResolver`,
`CurrentDomainAssemblyResolve`), wired through
`Microsoft.VisualStudio.TestPlatform.PlatformAbstractions.PlatformAssemblyResolver`, which references
`System.Runtime.Loader` — i.e. it hooks `AssemblyLoadContext.Default.Resolving`. When the default TPA bind
fails on the version check, that event fires and the resolver probes the output directory **by simple name,
ignoring version**, handing back the 0.1.0.0 file. `LoadFromAssemblyPath` performs no version check, so the
load succeeds.

I proved the same mechanism directly by adding a hand-written `AssemblyLoadContext.Default.Resolving` handler
to the throwaway app — it then behaves exactly like the test host:

```
  [shim] default bind failed for ZeroAlloc.Results, Version=0.1.4.0, ...; probing ...\ZeroAlloc.Results.dll
   -> ZeroAlloc.Results, Version=0.1.0.0, ...
ok
```

So `ApiHostSchedulingWiringTests` passes for the same reason `Daedalus.Tests.Playwright.Api`'s
`E2EServerFixture` gets far enough to fail on `relation "Skills" does not exist` — the test host silently
papers over the broken binding that a real host cannot.

---

## Step 3 — Current suite state (attribution baseline)

Run per-project on the unmodified tree at `dea73be`, `--no-build` after a clean solution build.

| Project | Passed | Failed | Total | Exit | Duration |
|---|---:|---:|---:|---:|---:|
| `Daedalus.Tests.Unit` | 149 | 0 | 149 | 0 | 10 s |
| `Daedalus.Tests.Unit.Domain` | 310 | 0 | 310 | 0 | 2 s |
| `Daedalus.Tests.Unit.Application` | 396 | 0 | 396 | 0 | 6 s |
| `Daedalus.Tests.Unit.Infrastructure` | 130 | 0 | 130 | 0 | 16 s |
| `Daedalus.Tests.Integration` | 435 | **9** | 444 | 1 | 127 s |
| `Daedalus.Tests.Playwright.Api` | 0 | **126** | 126 | 1 | 14 s |
| `Daedalus.Tests.Playwright.Browser` | 99 | 0 | 99 | 0 | 678 s |
| **Total** | **1519** | **135** | **1654** | | |

**This matches the recorded baseline of 1519 / 135 / 1654 exactly.** No drift to report.

**"Did not run" vs "passed":**

- The 126 `Daedalus.Tests.Playwright.Api` failures are **not 126 failing assertions**. All 126 fail in
  `OneTimeSetUp` with `Npgsql.PostgresException: 42P01: relation "Skills" does not exist`, thrown from
  `PostgresSkillStore.ListAsync` ← `Thalos.Skills.SkillSyncService.StartingAsync` during host start. The
  suite **did not run**; it has no green signal at all. `E2EServerFixture` starts the host before the
  schema exists — the pre-existing ordering bug, confirmed unchanged.
- The 9 `Daedalus.Tests.Integration` failures are all `AuthenticationFlowTests`
  (`AllProtectedEndpoints_RequireJwtToken` ×6, `ApiEndpoint_With{out,Valid,Invalid}Token_*` ×3), failing on
  a TCP connect while `traefik` holds port 8080. Environmental, confirmed unchanged.
- The remaining five suites genuinely ran and are green.

---

## Step 4 — The four options

### Root cause first, because it reframes all four

`ZeroAlloc.Results` package versions map to assembly versions like this (local cache, and confirmed by
downloading 1.2.0 and 1.2.1 fresh from nuget.org):

| Package | AssemblyVersion |
|---|---|
| 0.1.4 | 0.1.4.0 |
| 0.1.6 | 0.1.6.0 |
| 1.0.0 | 1.0.0.0 |
| 1.0.1 | 1.0.1.0 |
| 1.1.2 | 1.1.2.0 |
| 1.2.0 | 1.2.0.0 |
| **1.2.1** | **0.1.0.0** ← regression |

`1.2.1` is the latest published (nuget.org flat-container index confirms: no 1.2.2). Between 1.2.0 (commit
`5403528`) and the 1.2.1 release commit (`82f8186d`), the **only change under `src/`** is an analyzer package
bump:

```
modified src/ZeroAlloc.Results/ZeroAlloc.Results.csproj
-    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="4.14.0">
+    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="5.6.0">
```

No library source changed. And a metadata diff of the two shipped assemblies shows **identical public
surface** — 67 public/protected members each, zero added, zero removed. 1.2.0 and 1.2.1 are the same library.

**Why 1.2.1 is stamped wrong.** `ZeroAlloc.Results`' `Directory.Build.props` carries
`<VersionPrefix>0.1.0</VersionPrefix>` as the local fallback. The normal `release.yml` passes
`-p:Version=${version}` to *both* `dotnet build` and `dotnet pack`. The `publish-from-manifest.yml` rescue
workflow does not:

```yaml
- name: Build
  run: dotnet build --configuration Release --no-restore      # ← no -p:Version
- name: Pack every src/* csproj
  run: dotnet pack "$csproj" -c Release --no-build -p:PackageVersion="$VERSION" -o ./nupkg
```

That produces exactly what 1.2.1 is: package version 1.2.1, assembly version 0.1.0.0. `-p:PackageVersion`
alone never reaches `AssemblyVersion`, and `--no-build` means the already-mis-stamped binaries are packed.

**When it reached us.** `git log -S` on `Directory.Packages.props` puts it at `b7afe3e`
("feat(scheduling): run subagents as the configured detached principal through one seam", 2026-09-18 03:11),
which bumped Thalos.NET 0.4.0 → 0.5.0 and with it `ZeroAlloc.Results` **1.2.0 → 1.2.1**. The hosts booted
before that commit.

**Same defect elsewhere (latent).** Scanning the Api's resolved graph for package/assembly version
mismatches: `ZeroAlloc.Specification 1.1.0` also ships `0.1.0.0`, and `ZeroAlloc.Authorization 2.1.0` ships
`9.9.9.0`. Neither bites today — nothing references them above what they provide — but `Specification` is the
same failure waiting for its first 1.x-compiled consumer.

---

### Option 1 — Upstream rebuild. **Recommended as the durable fix; reachable.**

The brief frames this as "`ZeroAlloc.Authorization` recompiled against `ZeroAlloc.Results` 1.x". That is not
what is needed. `Authorization` is fine; it uses one type (`UnitResult<T>`) that exists unchanged in every
Results version from 0.1.4 to 1.2.1. What needs republishing is **`ZeroAlloc.Results` itself**, as 1.2.2,
built with `-p:Version=1.2.2` so the assembly is stamped `1.2.2.0`.

**Is it reachable?** Yes, on both counts the brief could not know:

- All of these packages are authored by the same person as this repo (`<authors>Marcel Roozekrans</authors>`
  in `ZeroAlloc.Results`, `ZeroAlloc.Authorization`, `Thalos.NET.Abstractions`, `AI.Sentinel`).
- The source is **on this machine**: `C:\Projects\Prive\ZeroAlloc\ZeroAlloc.Results` (a clone at `acf5cde`,
  just past the v1.2.0 tag), with the GitHub remote `ZeroAlloc-Net/ZeroAlloc.Results`.

The fix upstream is two parts: (a) cut 1.2.2 through `release.yml`, and (b) repair
`publish-from-manifest.yml` to pass `-p:Version="$VERSION"` to its build step, since that workflow is shared
across the ZeroAlloc org and has already mis-stamped `ZeroAlloc.Specification 1.1.0` too.

**Verdict: correct and reachable, but it is a cross-repo release cycle.** It should happen, and it is not
something this phase should block on.

### Option 2 — A binding shim. **Works; rejected as the primary fix.**

I built it and ran it (output quoted in step 2): an `AssemblyLoadContext.Default.Resolving` handler probing
the app directory by simple name resolves `0.1.4.0` → the on-disk `0.1.0.0` and the process continues. The
brief's stated risk — redirecting onto an incompatible assembly — **does not apply here**, and I checked
rather than assumed: `Authorization` needs only `UnitResult<T>` (+ its `Empty`), `AI.Sentinel` needs
`Result<,>`, `UnitResult<>` and `ResultExtensions.Match`, `Rag.NET.Abstractions` needs `Result<,>`. All are
present, and 1.2.0/1.2.1's public surfaces are identical. The shimmed run resolves
`IAuthorizationPolicy.EvaluateAsync` to `ValueTask<UnitResult<AuthorizationFailure>>` correctly.

**Rejected anyway**: it is startup code in every host (Api, Cli, Console) that must run before the first
triggering JIT, it permanently hides version mismatches including future real ones, and it buys nothing that
option 4 does not buy with one attribute and no code.

### Option 3 — Drop the dependency. **Not viable.**

`ZeroAlloc.Authorization` is not ours to drop. It arrives transitively:

```
AI.Sentinel/2.3.0            -> ZeroAlloc.Authorization 2.1.0
Thalos.NET.Abstractions/0.5.0 -> ZeroAlloc.Authorization 2.1.0
```

and `ISecurityContext` is a **Thalos contract type** — `Thalos.NET.Abstractions`' own TypeRef table points at
`ZeroAlloc.Authorization.ISecurityContext [v9.9.9.0]`. It appears in Thalos signatures we consume directly
(e.g. `IAgentRuntime.CreateSessionAsync(AgentId, ISecurityContext, …)`). Our two implementations
(`ClaimsSecurityContext`, `DetachedPrincipal`, ~80 lines total) are trivial to move; the dependency is not.
Dropping it means forking `Thalos.NET.Abstractions` and `AI.Sentinel`. Out of all proportion to the problem.

### Option 4 — Pin `Results` down. **Recommended as the immediate unblock. Verified.**

**Does Thalos.NET 0.5.0 genuinely require 1.2.x, or merely floor it?** I read the nuspecs rather than
inheriting the claim. `Thalos.NET` itself declares *no* dependency on `ZeroAlloc.Results`. The floor comes
from `Thalos.NET.Abstractions 0.5.0`:

```
thalos.net.abstractions/0.4.0 :: id="ZeroAlloc.Results" version="1.2.0"
thalos.net.abstractions/0.5.0 :: id="ZeroAlloc.Results" version="1.2.1"
zeroalloc.authorization/2.1.0 :: id="ZeroAlloc.Results" version="0.1.4"
ai.sentinel/2.3.0             :: id="ZeroAlloc.Results" version="1.2.0"
rag.net.abstractions/1.0.0    :: id="ZeroAlloc.Results" version="1.2.0"
```

Unbracketed, so **a floor, not a lock** — `>= 1.2.1`, and every other consumer floors at 1.2.0 or lower. So
1.2.0 satisfies everything except 0.5.0's floor, which is one patch too high and, as shown above, points at a
package whose only difference from 1.2.0 is an analyzer bump.

There is one real obstacle, and it is not NU1605. With
`CentralPackageTransitivePinningEnabled=true`, lowering the `PackageVersion` produces **NU1109**, an error:

```
error NU1109: Detected package downgrade: ZeroAlloc.Results from 1.2.1 to centrally defined 1.2.0.
error NU1109:  Daedalus.Api -> Thalos.NET.Abstractions 0.5.0 -> ZeroAlloc.Results (>= 1.2.1)
```

and NU1109 is **not suppressible** — I added `NU1109` to `NoWarn` in `Directory.Build.props` and it still
failed with 9 errors.

The mechanism that does work is `VersionOverride` on an explicit `PackageReference`, which NuGet treats as a
deliberate choice rather than an accidental downgrade:

```xml
<PackageReference Include="ZeroAlloc.Results" VersionOverride="1.2.0" />
```

It is **per-project and does not flow across `ProjectReference`**, so it has to go in each project that
produces an output — I confirmed that adding it only to `Daedalus.Agents` left `Daedalus.Api` resolving 1.2.1.
With it in `Daedalus.Agents`, `Daedalus.Api`, `Daedalus.Cli`, `Daedalus.Console` and all seven test projects:

```
dotnet build Daedalus.sln -c Debug --no-incremental
    0 Error(s)
    0 Warning(s)        (with TreatWarningsAsErrors=true)

Daedalus.Api                       ZeroAlloc.Results/1.2.0 [{'assemblyVersion': '1.2.0.0', ...}]
Daedalus.Cli                       ZeroAlloc.Results/1.2.0 [{'assemblyVersion': '1.2.0.0', ...}]
Daedalus.Console                   ZeroAlloc.Results/1.2.0 [{'assemblyVersion': '1.2.0.0', ...}]
Daedalus.Tests.*                   ZeroAlloc.Results/1.2.0 [{'assemblyVersion': '1.2.0.0', ...}]
```

`1.2.0.0` is `>=` all three requested versions (0.1.0.0, 0.1.4.0, 1.2.0.0), so every ref binds.

**The API host then boots and serves:**

```
info: Thalos.Memory.RagNet.RagNetMemorySchemaInitializer[541] Rag.NET memory index schema ready (rag_chunks, vector(768))
info: Thalos.Skills.SkillSyncService[560]                     Skill sync: 2 scanned, 2 upserted, 0 unchanged, 0 skipped, 0 deactivated
info: Daedalus.Agents.Scheduling.ScheduleReconciler[443]      Reconciled 1 configured schedule(s) into the ScheduledRuns table.
...
HTTP /health    -> 200   Healthy
HTTP /scalar/v1 -> 200
```

(`ASPNETCORE_ENVIRONMENT=Development`, `ConnectionStrings__daedalus` pointed at the `daedalus_postgres`
container with its `daedalus`/`daedalus` credentials, `ASPNETCORE_URLS=http://localhost:5199` to avoid
traefik on 8080. In `Production` the same host boots but returns 500 from
`RequireHttpsMetadata` on the Keycloak authority — unrelated to this blocker.)

**Full suite with the fix applied, identical run procedure as the baseline:**

| Project | Passed | Failed | Total |
|---|---:|---:|---:|
| `Daedalus.Tests.Unit` | 149 | 0 | 149 |
| `Daedalus.Tests.Unit.Domain` | 310 | 0 | 310 |
| `Daedalus.Tests.Unit.Application` | 396 | 0 | 396 |
| `Daedalus.Tests.Unit.Infrastructure` | 130 | 0 | 130 |
| `Daedalus.Tests.Integration` | 435 | 9 | 444 |
| `Daedalus.Tests.Playwright.Api` | 0 | 126 | 126 |
| `Daedalus.Tests.Playwright.Browser` | 99 | 0 | 99 |
| **Total** | **1519** | **135** | **1654** |

A `diff` of the *set of failing test names* between the baseline run and the verification run returns
**IDENTICAL**. No regression, no new failure, nothing newly green that shouldn't be.

---

## Recommendation

**Adopt option 4 now, and open option 1 upstream in parallel.**

1. **Immediate (inside this repo, ~10 lines, one commit):** add
   `<PackageReference Include="ZeroAlloc.Results" VersionOverride="1.2.0" />` to `Daedalus.Agents`,
   `Daedalus.Api`, `Daedalus.Cli`, `Daedalus.Console` and the seven test projects, with a comment naming the
   reason (1.2.1 ships `AssemblyVersion 0.1.0.0`; remove when 1.2.2 lands). Keep the test projects in the set
   so the test graph matches the production graph — without them the suite runs against 1.2.1 and keeps
   hiding this class of bug. This is Task-1-adjacent work, not Task 1: it should be its own small task/commit
   ahead of Task 2.
2. **Durable (other repo):** publish `ZeroAlloc.Results` 1.2.2 from `release.yml`, and fix
   `publish-from-manifest.yml` to pass `-p:Version="$VERSION"` to its build step across the ZeroAlloc org.
   Then delete the `VersionOverride` lines. Also re-stamp `ZeroAlloc.Specification` (1.1.0 → `0.1.0.0`) before
   it bites, and sanity-check `ZeroAlloc.Authorization`'s `9.9.9.0`.

**Consequence for this phase:** the ui-review audit is *not* blocked. The API host boots, listens and answers
`200` once this pin is in. Task 10 step 3 should not report BLOCKED on this account.

### Two findings the phase should know about

1. **`Daedalus.Cli` has a second, independent boot failure**, previously masked by this one. With the pin
   applied it gets past assembly loading and dies in DI validation:

   ```
   Unable to resolve service for type 'Daedalus.Application.Abstractions.IProjectRepository'
   while attempting to activate 'Daedalus.Infrastructure.Services.WorkspaceOrchestrator'.
   ```

   `IProjectRepository` is registered in `src/Daedalus.Api/Program.cs:93` and
   `src/Daedalus.Console/Program.cs:37`, but **not** in `src/Daedalus.Cli/Program.cs`. Pre-existing, unrelated
   to this task, and it does not affect ui-review (which needs Api + Web). Worth its own ticket.
2. **`Daedalus.Tests.Playwright.Api` is 126 tests that never execute**, not 126 assertions that fail. Any
   attribution during this phase should treat that suite as having no signal at all until the
   `E2EServerFixture` schema-ordering bug is fixed.

---

## Step 5 — Throwaway deleted

Everything built for this investigation is gone.

- Repo edits (temporary `ZeroAlloc.Results` 1.2.0 pin, `NoWarn;NU1109` experiment, `VersionOverride` lines in
  4 src + 7 test csprojs) reverted with `git checkout -- Directory.Packages.props Directory.Build.props src tests`.
- Solution rebuilt at `HEAD`: clean (0 errors, 0 warnings), and `Daedalus.Api.deps.json` back to
  `ZeroAlloc.Results/1.2.1 [assemblyVersion 0.1.0.0]` — i.e. the tree is exactly as found, still broken.
- Scratch projects (`repro/App`, `repro/Tests`), the downloaded/extracted nupkgs and the metadata probe script
  lived only under the session scratchpad, never in the repo, and have been removed.
- No commits were created — `HEAD` is `dea73be`, unchanged from before the experiment. Nothing was written
  to `src/`.

`git status --short`:

```
?? docs/regression-report-2026-03-01-1800.md
?? docs/regression-screenshots/2026-03-01-1800/
```

Those two are the pre-existing untracked files I was told to leave alone. This report lives under
`.superpowers/sdd/`, which is ignored by `.superpowers/sdd/.gitignore`. `git clean` was never run.
