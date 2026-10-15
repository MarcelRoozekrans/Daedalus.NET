# Regression Report: Phase 2.13 navigation and flow

| Metric | Value |
|---|---|
| Date | 2026-10-15 20:26 UTC (sweep 19:50–20:26) |
| Application URL | http://localhost:5290, the Aspire stack, branch `phase/2.13-navigation-flow` at cbdcd997 |
| Signed in as | realm users `dev` (developer, task-manager, project-manager, analyst) and `admin` through Keycloak |
| Pages tested | 12: `/` signed out and signed in, `/tasks`, `/runs`, `/runs/{id}` not found, `/projects`, `/prd-generator`, `/agent`, `/schedules`, `/costs`, `/repositories` as admin, `/executions` removed. Also the Create task dialog |
| Viewports tested | 3: desktop 1920x1080, tablet 768x1024, mobile 375x812 |
| Existing tests passed | Unit.Domain 356, Unit.Application 271, Unit.Infrastructure 251, Unit 1644, Integration 735, Playwright.Api 115, Playwright.Browser 147 |
| Existing tests failed | 0 product failures; see Existing test results for the two environment faults found and cleared |
| Console errors found | 0 unexpected. The expected ones: the 404 for an unknown run, the 422 of a refused manufacture, and the 403 on `/repositories` as developer, which is a finding |
| Network errors found | 1 finding: `GET /api/repositories` 403 shown as an empty list |
| Visual issues found | 10: 4 Major, 6 Minor; all recorded for the phase, none fixed in this sweep |
| Overall status | WARN |

Compared with the 2.12 sweep (`docs/regression-report-2026-10-13-1033.md`): every 2.12 finding is fixed except one, the
Process filter, which is carried as the accepted deviation R-B7-process. See "The 2.12 findings" below.

## Browser

The Playwright MCP server could not launch. It asks for the `chrome` channel at `/opt/google/chrome/chrome`, which is
not installed, so the extracted Chromium libraries cannot help it. The sweep drove the same Playwright library, 1.64,
from Node instead, with the cached `chromium_headless_shell-1248`. It used `LD_LIBRARY_PATH` and `FONTCONFIG_FILE` from
the scratchpad's `chromelibs` and `fonts.conf`. The full `chromium-1208` build fails with `libcups.so.2` missing.
Passwords were read from `keycloak-realm.json` at run time and are not recorded anywhere.

## Existing test results

Every suite ran without a rebuild, against the build the stack also serves, one suite at a time while the stack ran.

| Suite | Passed | Failed | Notes |
|---|---|---|---|
| Unit.Domain | 356 | 0 | |
| Unit.Application | 271 | 0 | |
| Unit.Infrastructure | 251 | 0 | |
| Unit | 1644 | 0 | |
| Integration | 735 | 0 | The first run failed 100. Cause: `IOException: The configured user limit (128) on the number of inotify instances has been reached`. The running Aspire stack holds most of the host's 128 inotify instances, and each test host's `PhysicalFileProvider` wants one more. Rerun with `DOTNET_USE_POLLING_FILE_WATCHER=1`: 735 of 735 |
| Playwright.Api | 115 | 0 | |
| Playwright.Browser | 147 | 0 | The first run failed 140 of 147. The browser saw `SRI's integrity checks failed` on every `_framework/*.wasm`. The test project's `Daedalus.Web.staticwebassets.*.json` came from the 21:46 CEST build. The AppHost rebuilt `Daedalus.Web` at 21:48 CEST when the stack started, which changed the fingerprinted files. The fix was to rebuild the test project alone with `dotnet build --no-dependencies`, then run it with `DOTNET_USE_POLLING_FILE_WATCHER=1`: 147 of 147 |

Neither fault is in the product. The second one is worth knowing when the browser suite runs beside a running AppHost.

## Page-by-page results

Screenshots: `docs/plans/ui-review-2026-10-15-phase-2.13/{page}-{desktop,tablet,mobile}.png`, full page. At every
width, every page was measured for `scrollWidth` against `clientWidth`. **No page overflows at any width**, PRD Generator
included; in 2.12 it was 197px too wide at 375.

| Page | Desktop | Tablet | Mobile | Notes |
|---|---|---|---|---|
| `/` signed out | ![](plans/ui-review-2026-10-15-phase-2.13/landing-signed-out-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/landing-signed-out-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/landing-signed-out-mobile.png) | Pass with a minor: name, sentence, one Sign in; no sidebar, no toggle. The header's logo and Sign in sit at the top edge of the header instead of centred |
| `/` signed in | ![](plans/ui-review-2026-10-15-phase-2.13/home-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/home-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/home-mobile.png) | Pass. Four cards in urgency order, 2x2 on desktop and one column below. One New task button. Empty lines on a fresh database |
| `/tasks` | ![](plans/ui-review-2026-10-15-phase-2.13/tasks-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/tasks-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/tasks-mobile.png) | Pass with minors. The contracted columns are there; Priority and Created are hidden at 768; Manufacture is labelled. An empty grid says "No records to display.". At 375 only Title, truncated, and Project are visible before scrolling the grid. "Create Task" wraps to two lines at 375 |
| Create task dialog | ![](plans/ui-review-2026-10-15-phase-2.13/task-create-dialog-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/task-create-dialog-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/task-create-dialog-mobile.png) | Major. The flow works: create with three fields, errors under the fields, and a refused manufacture that names its reason (`finding-create-and-manufacture-refused-desktop.png`). On a phone the dialog is full width with the primary button on top. But Title, Description and Prompt have no accessible name, and Project's is a GUID |
| `/runs` | ![](plans/ui-review-2026-10-15-phase-2.13/runs-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/runs-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/runs-mobile.png) | Pass. Empty state "No runs yet. Start one with Manufacture on a board task." |
| `/runs/{id}` not found | ![](plans/ui-review-2026-10-15-phase-2.13/run-not-found-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/run-not-found-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/run-not-found-mobile.png) | Pass. "Run not found. Back to Runs". No run exists on this stack, so the run page with data is covered by the browser suite only |
| `/projects` | ![](plans/ui-review-2026-10-15-phase-2.13/projects-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/projects-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/projects-mobile.png) | Pass; the grid scrolls inside its box. Its create dialog has the same unlabelled-field defect, and it predates the phase |
| `/prd-generator` | ![](plans/ui-review-2026-10-15-phase-2.13/prd-generator-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/prd-generator-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/prd-generator-mobile.png) | Pass. Fixed since 2.12: no overflow at 375 |
| `/agent` | ![](plans/ui-review-2026-10-15-phase-2.13/agent-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/agent-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/agent-mobile.png) | Major at 375. The heading is fixed since 2.12, but the page stays two columns at 375, and the empty-state column is about 70px wide with one word per line |
| `/schedules` | ![](plans/ui-review-2026-10-15-phase-2.13/schedules-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/schedules-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/schedules-mobile.png) | Pass |
| `/costs` | ![](plans/ui-review-2026-10-15-phase-2.13/costs-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/costs-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/costs-mobile.png) | Pass; the tables scroll inside their cards at 375 |
| `/repositories`, admin | ![](plans/ui-review-2026-10-15-phase-2.13/repositories-desktop.png) | ![](plans/ui-review-2026-10-15-phase-2.13/repositories-tablet.png) | ![](plans/ui-review-2026-10-15-phase-2.13/repositories-mobile.png) | Pass for admin. As developer, by URL, it is Major: see `finding-repositories-as-developer-desktop.png` |
| `/executions` | ![](plans/ui-review-2026-10-15-phase-2.13/executions-removed-desktop.png) | — | — | Removed as planned; the bare router "Sorry, there's nothing at this address." |

Other evidence: `menu-overlay-mobile.png` (phone menu over the page with a backdrop), `user-menu-mobile.png` (compact
header menu), `loading-screen-desktop.png` (no gradient), `task-edit-dialog-desktop.png`,
`finding-task-create-validation-desktop.png`.

## The four checks deferred to this sweep

1. **Keycloak round trip: pass.** Signed out, `/tasks` goes to `/authentication/login`, then Keycloak `auth`, then
   `/authentication/login-callback`, then `/tasks`. Sign in from the landing returns to `/`. Log out lands on the
   signed-out landing, at `/?state=…`; the leftover query string is a minor.
2. **Expired session: pass, with a minor.** The test signed in as `dev` on `/tasks`, then ended the Keycloak session in
   a second tab through the realm's logout endpoint, then waited. When the 300s access token ran out, the page's next
   API call failed to refresh. The Info notification "Your session has ended. Signing in again." appeared at 301.4s and
   the redirect to Keycloak followed at 301.5s. After signing in, the page was back on `/tasks`. There was no Error
   toast and no console error. The notification is on screen for only about 0.1s before the page navigates, so nobody
   can read it.
3. **Cold load signed in: pass, with a minor.** Each change to the header and `#app` was recorded per animation frame:
   - from 0ms, the loading screen ("Daedalus" and the ring);
   - at about 500ms, the logo only, with "Checking authentication…";
   - at about 720ms, the page content with the logo-only header;
   - at about 780ms, the toggle, "Dev User", Log out and the sidebar.

   Sign in and an empty menu never appear. But for about 55ms the page renders full width with no sidebar, then shifts
   260px right on desktop.
4. **Admin and non-admin menu: pass.** `admin` sees an "Admin" divider and Git Repositories; `dev` does not. `dev`
   opening `/repositories` directly gets a 403 shown as "No Repositories Configured" with an Add repository button, a
   Major finding.

## The 2.12 findings

| 2.12 finding | Source | Status in 2.13 |
|---|---|---|
| Sidebar toggle white on white header | review 1, sweep 1 | **Fixed** in 2.12 and holds: visible at every width |
| Phone: the open sidebar pushes the content; the header wraps | review 2, sweep 4, #349 | **Fixed**. At 375 the menu overlays with a backdrop and the body stays at x=0, width 375. It closes on navigation, backdrop and Escape, with a focus trap. The compact header has a user icon menu |
| Process filter is a text box | review 3 | **Carried**, accepted deviation R-B7-process; no issue, since it was ruled not a defect. It is still a `RadzenTextBox` |
| A failed run shows no reason | review 4, #347 | **Fixed** in code and the browser suite. `RunHeader`'s Failed alert shows `failureReason` or the fallback line. Not seen live: no run on this stack |
| NONE outcome badges | review 5, sweep 7 | **Fixed**: `RunStatusLabels.ShowsStepBadge` hides `none` outcomes |
| Browser-default PR link | review 5, sweep 7 | **Fixed**: a `RadzenLink` in `--rz-primary` with `open_in_new`, "opens in a new tab" |
| GUID in "started by" | review 5, sweep 7 | **Fixed**: `RunStarter.Name(StartedByName)`, falling back to "unknown" |
| Solid and lighter badge shades between Tasks and Runs | review 5, sweep 7 | **Fixed**: both use `RunStatusBadge`, a solid pill |
| Create Task 400s silently on Parallel Group 0 | sweep 2, #348 | **Fixed**: a task created with project, title and description succeeds |
| Raw Project GUID, required Prompt, Ralph-era fields | sweep 3, #348 | **Fixed**: a project picker, an optional Prompt under Advanced, and the four fields gone |
| PRD Generator overflows 197px at 375 | sweep 5, #349 | **Fixed**: `scrollWidth` 375 |
| Stale session stays "signed in"; signed-out `/tasks` says "Please log in" | sweep 6, #350 | **Fixed**: see deferred checks 1 and 2 |
| Manufacture icon-only, named "precision_manufacturing" | sweep 7 | **Fixed**: visible "Manufacture" text and the accessible name "Manufacture" |
| `/executions` is the Ralph "Task Loop History" | sweep 8, #351 | **Fixed**: removed from the menu and the app |
| `/agent` has no heading | sweep 8, #351 | **Fixed**: `H4` "Agent" with a caption |
| Home's four coloured quick actions and heading icon | sweep 8, #351 | **Fixed**: one New task button, no icon |
| Loading screen is a full-viewport gradient | sweep 9, #351 | **Fixed**: body background, name and a primary ring |

Count: 16 fixed, 1 carried as R-B7-process. Issues #347 to #351 and #354 are still open on GitHub, because they close
when this branch merges.

## Recommendations

1. **Major.** Create and Edit task, every width: give each `RadzenFormField` input an accessible name. Today the labels
   have `for=""`. Title, Description and Prompt are unnamed, Project's `aria-label` is a GUID, and Priority and
   Complexity read "1". The Projects dialog has the same defect.
2. **Major.** Text contrast below the contract's 4.5:1:
   - page captions at `opacity: 0.6`, 3.29:1, in 11 files;
   - the Manufacture label, white on `--rz-success`, 2.54:1;
   - primary buttons, white on `--rz-primary`, 3.66:1;
   - Priority badges, 3.68:1;
   - sidebar dividers, 2.61:1.

   The last three are token-level, so they need a decision rather than a page fix.
3. **Major.** `/agent` at 375 stays two columns, with a 70px empty-state column.
4. **Major.** `/repositories` as a non-admin shows a 403 as "No Repositories Configured" and offers Add repository.
5. **Minor.** Tasks' empty grid shows "No records to display." instead of the contracted "No tasks yet." plus the
   action.
6. **Minor.** The signed-out header's logo and Sign in are not vertically centred.
7. **Minor.** The session-ended notification shows for about 0.1s before the redirect.
8. **Minor.** On a cold load, the content renders about 55ms before the sidebar, then shifts 260px.
9. **Minor.** Wording and consistency:
   - the action is "New task" on Home and "Create Task" on Tasks;
   - "Create Task" and "Create Project" wrap at 375;
   - the footer still says "Autonomous Code Analysis Platform";
   - the post-logout URL keeps `?state=`;
   - unknown paths get the bare router not-found text.
10. **Minor.** Tasks at 375: Status and Manufacture are reachable only by scrolling the grid sideways.
11. **Test environment.** Run the browser suite with `DOTNET_USE_POLLING_FILE_WATCHER=1`, and rebuild the test project
    after the AppHost has rebuilt `Daedalus.Web`, or the static-asset manifest is stale.
