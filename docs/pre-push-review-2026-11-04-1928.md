# Pre-push review: phase 3.4, controllers to minimal APIs

| Metric | Value |
|---|---|
| Date | 2026-11-04 19:28 |
| Branch | `phase/3.4-minimal-apis` |
| Base Branch | `origin/main`, merge-base `623106f7` |
| Commits Reviewed | 71, through `c754d7d0` |
| Files Changed | 174 |
| Lines Added | 15,426 |
| Lines Removed | 5,134 |
| Verdict | **PASS** |

The review ran on `35784a12`. Its findings were fixed in the loop, in `e40ddc08` (rulings doc) and `c754d7d0`
(comments). The counts above include both commits.

## Plan adherence

Plan: `docs/superpowers/plans/2026-10-31-phase-3.4-minimal-apis-plan.md`, with rulings
`docs/plans/2026-10-31-phase-3.4-rulings.md`.

- Tasks 1 to 21 have their changes in the diff. All 13 controllers are gone (`src/Daedalus.Api/Controllers` no
  longer exists) and every route is served from `src/Daedalus.Api/Endpoints/`. `Program.cs` calls
  `AddDaedalusEndpoints` and `MapDaedalusEndpoints`, and no longer calls `AddControllers`, `MapControllers`,
  `AddApiVersioning` or the `IsEnhancedModelMetadataSupported` switch.
- Task 20's grep gate gives one hit, an XML comment in `Directory.Packages.props:227` recording the
  `Asp.Versioning.Mvc` removal. That is not code.
- Owner decision "14 catch-alls stay": there are exactly 14 `catch (Exception` blocks in `Endpoints/`.
- Task 21's `grep "Controller"` over CLAUDE.md, README.md, the development guide and the architecture diagrams
  finds no hits. MILESTONE.md and ROADMAP.md both say 13 controllers.
- Task 22 is in progress: steps 1 to 4 are this run, and step 5 (PR, pause-work) is the controller's.
- Unplanned: none that matters. The test folders `tests/*/Controllers/` keep their names and namespaces. The design
  says "names may stay", and the final fix round kept them on purpose. Info only.

## Code quality

| # | Rule | File:line | Finding | Severity | Status |
|---|---|---|---|---|---|
| 1 | 6 Naming/readability | `src/Daedalus.Api/Endpoints/ErrorBody.cs:4` | Says "the body the controllers write", in the present tense, after MVC is gone | Warning | Fixed `c754d7d0` |
| 2 | 6 Naming/readability | `src/Daedalus.Api/Endpoints/OpenApiQueryParameters.cs:12` | Says "the controllers' remaining `[FromQuery]` parameters" | Warning, grouped with 1 | Fixed `c754d7d0` |
| 3 | 6 Naming/readability | `src/Daedalus.Web/Services/WebJsonContext.cs:22` | Says "most controllers return". `35784a12` fixed the same phrase in `ApiClient.cs` but missed this one | Warning, grouped with 1 | Fixed `c754d7d0` |
| 4 | 6 Docs | `.github/copilot-instructions.md` | The superseded file still teaches `ControllerBase`/`IActionResult` and has no phase 3.4 note. Phase 2.8 had set the precedent of adding one | Info | Fixed `c754d7d0`: banner added |
| 5 | Plan/rulings | `docs/plans/2026-10-31-phase-3.4-rulings.md` R41, R42 | R41's cost line covered one of its three choices, and R42's left out the auth column. Ten lines were over 120 characters | Warning, grouped with 1 | Fixed `e40ddc08` |

Checked with no finding:
- **Security:** no new secrets. The one `secret` match is a test summary's prose.
- **Debug code:** none (`Console.Write`, `Debugger`, TODO/FIXME).
- **Suppressions:** no `#pragma warning disable`, `NoWarn` or `SuppressMessage` was added. The only matches are in the
  plan's own rule text.
- **Reflection-based JSON:** no `TypedResults.Json` or `Results.Json` in `src`.
- **Skipped tests:** no `Skip =` was added.

Findings 1 to 3 and 5 count as one stale-wording warning, now fixed.

## Commit hygiene

- **Subjects:** all are Conventional Commits except the merge commit `95dc700c`, which is allowed. The header cap is
  100, and none is over it.
- **Signatures:** no `Co-Authored-By`, no Claude signature, no session URL.
- **Nested parentheses:** none in any message.
- **Body lines over 100 characters:** one, in `ac306f61` ("Share one api/ endpoint helper…", 167 characters).
  commitlint's `body-max-line-length` is off (level 0), so CI passes. The commit is already pushed, and a rewrite
  would mean force-pushing shared history. Info: when squash-merging, wrap that line in the squash body.
- **Secrets scan:** clean.
- **Conflict markers:** none.
- **Unintended files:** none (no bin/obj/.env/logs).
- **Large files:** no added file is over 100 KB.
- **Private information:** none (no LAN IPs, hostnames or home paths).

## Regression tests

Run on `35784a12` in Release, one project at a time. The build has 0 warnings and 0 errors.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Unit | 2437 | 0 | 0 |
| Unit.Domain | 356 | 0 | 0 |
| Unit.Application | 304 | 0 | 0 |
| Unit.Infrastructure | 278 | 0 | 0 |
| Integration | 832 | 0 | 0 |
| Playwright.Api | 115 | 0 | 0 |
| Playwright.Browser | 219 | 0 | 0 |

The known environmental cases, `ManufactureSandboxEndToEndTests` and the flaky `AppHostOllamaWiringTests` Keycloak
test, both passed this run.

The fixes after the review change only comments and docs. The Release build stayed at 0 warnings,
`dotnet format --verify-no-changes` is clean on the touched files, and Unit was re-run: 2437 passed.

UI contract audit: no ui-contract for this phase. Browser regression is covered by Playwright.Browser.

## Verdict

**PASS**: 0 blockers. One warning (stale controller wording, now fixed) and three info items. Nothing is open.
