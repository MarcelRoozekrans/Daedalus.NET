# Regression Report: Phase 2.12 live proof

| Metric | Value |
|---|---|
| Date | 2026-10-13 10:33 UTC |
| Application URL | http://localhost:5290 (Aspire stack, branch `feat/phase-2.12-manufacturing-console` at 45db27b plus the sidebar-toggle colour fix) |
| Signed in as | realm user `dev` (developer, task-manager, project-manager, analyst) through Keycloak |
| Pages tested | 11 (`/`, `/tasks`, `/runs`, `/runs/{id}` succeeded and failed, `/projects`, `/executions`, `/schedules`, `/costs`, `/agent`, `/prd-generator`) |
| Viewports tested | 3 (desktop 1920x1080, tablet 768x1024, mobile 375x812) |
| Existing tests passed | Unit 1285, Integration 701, Playwright.Api 113, Playwright.Browser 97 |
| Existing tests failed | 0 |
| Console errors found | 0 in a fresh session (a stale session after a Keycloak restart logs token 400s; see findings) |
| Network errors found | 0 |
| Visual issues found | 9 (1 fixed in this sweep, 8 recorded for phase 2.13) |
| Overall status | WARN |

## Existing test results

Run on the branch head before the sweep, one suite at a time: build 0 warnings; Unit.Domain 346, Unit.Application 261,
Unit.Infrastructure 241, Unit 1285, Integration 701, Playwright.Api 113, Playwright.Browser 97; 0 failed, 0 skipped; `dotnet
format --verify-no-changes` clean on every changed `.cs` file.

## Page-by-page results

Screenshots: `docs/regression-screenshots/2026-10-13-1033/{page}-{desktop,tablet,mobile}.png`, full page.

| Page | Desktop | Tablet | Mobile | Notes |
|---|---|---|---|---|
| `/runs` | ![](regression-screenshots/2026-10-13-1033/runs-desktop.png) | ![](regression-screenshots/2026-10-13-1033/runs-tablet.png) | ![](regression-screenshots/2026-10-13-1033/runs-mobile.png) | Pass. Tablet hides started by and updated; the grid scrolls inside its card on tablet and phone. |
| `/runs/23e8b6ff` (succeeded) | ![](regression-screenshots/2026-10-13-1033/run-succeeded-desktop.png) | ![](regression-screenshots/2026-10-13-1033/run-succeeded-tablet.png) | ![](regression-screenshots/2026-10-13-1033/run-succeeded-mobile.png) | Pass. Header with "Pull request #18"; tabs scroll on phone. |
| `/runs/e47eeac0` (failed) | ![](regression-screenshots/2026-10-13-1033/run-failed-desktop.png) | ![](regression-screenshots/2026-10-13-1033/run-failed-tablet.png) | ![](regression-screenshots/2026-10-13-1033/run-failed-mobile.png) | Pass. The cap step reads CapExceeded (Danger). No failure reason is shown: #347. |
| `/tasks` | ![](regression-screenshots/2026-10-13-1033/tasks-desktop.png) | ![](regression-screenshots/2026-10-13-1033/tasks-tablet.png) | ![](regression-screenshots/2026-10-13-1033/tasks-mobile.png) | Pass for 2.12 (badge links to run). Titles truncate while Prompt takes the width; Iterations column is always "—". |
| `/` | ![](regression-screenshots/2026-10-13-1033/dashboard-desktop.png) | ![](regression-screenshots/2026-10-13-1033/dashboard-tablet.png) | ![](regression-screenshots/2026-10-13-1033/dashboard-mobile.png) | Minor: four differently coloured full-width buttons, an icon beside "Quick Actions". |
| `/projects` | ![](regression-screenshots/2026-10-13-1033/projects-desktop.png) | ![](regression-screenshots/2026-10-13-1033/projects-tablet.png) | ![](regression-screenshots/2026-10-13-1033/projects-mobile.png) | Pass. |
| `/executions` | ![](regression-screenshots/2026-10-13-1033/executions-desktop.png) | ![](regression-screenshots/2026-10-13-1033/executions-tablet.png) | ![](regression-screenshots/2026-10-13-1033/executions-mobile.png) | Minor: titled "Task Loop History", asks for "a filter type and an ID" — Ralph-era. |
| `/schedules` | ![](regression-screenshots/2026-10-13-1033/schedules-desktop.png) | ![](regression-screenshots/2026-10-13-1033/schedules-tablet.png) | ![](regression-screenshots/2026-10-13-1033/schedules-mobile.png) | Pass. |
| `/costs` | ![](regression-screenshots/2026-10-13-1033/costs-desktop.png) | ![](regression-screenshots/2026-10-13-1033/costs-tablet.png) | ![](regression-screenshots/2026-10-13-1033/costs-mobile.png) | Pass. |
| `/agent` | ![](regression-screenshots/2026-10-13-1033/agent-desktop.png) | ![](regression-screenshots/2026-10-13-1033/agent-tablet.png) | ![](regression-screenshots/2026-10-13-1033/agent-mobile.png) | Minor: no page heading. |
| `/prd-generator` | ![](regression-screenshots/2026-10-13-1033/prd-generator-desktop.png) | ![](regression-screenshots/2026-10-13-1033/prd-generator-tablet.png) | ![](regression-screenshots/2026-10-13-1033/prd-generator-mobile.png) | Major: the content overflows its column by 197px at 375px. |

Measured at 375px on every page: content column 374px wide, heading at x=33, no page-level horizontal overflow — the
width fix of this phase holds app-wide.

## Recommendations

1. **Fixed in this sweep — Major.** The sidebar toggle was white on the white header at every width
   (`--rz-sidebar-toggle-color` defaults to `--rz-on-primary` in Radzen Material): below 1280px, where the sidebar starts
   collapsed, there was no visible way to the menu. `daedalus-theme.css` now points it at `--rz-header-color`; a browser
   test asserts a contrast of at least 3:1.
2. **Major (2.13).** Create Task with its defaults fails 400 silently: the form's Parallel Group defaults to 0, the API
   requires at least 1, and the dialog shows no error.
3. **Major (2.13).** Create Task asks for a raw Project ID GUID with no picker, requires a Prompt that repeats the
   description, and carries Ralph-era fields (Phase, Parallel Group, Dependencies, Files to Modify).
4. **Major (2.13).** On a phone, the opened sidebar pushes the content aside instead of overlaying it; the page shrinks
   to about 100px. The header wraps the user name and "Log out".
5. **Major (2.13).** `/prd-generator` overflows 197px at 375px.
6. **Minor (2.13).** A session whose Keycloak session ended (a restarted realm) still shows the user as signed in; every
   token refresh fails with 400 and nothing prompts a new sign-in. Signed-out `/tasks` shows "Please log in to access
   this data" instead of redirecting.
7. **Minor (2.13).** "Started by" shows the subject GUID, not a name; steps whose outcome is `none` show a NONE badge;
   the PR link uses the browser's default link style; the Manufacture button is icon-only with the accessible name
   "precision_manufacturing"; status badges are solid on Tasks and the lighter shade on Runs.
8. **Minor (2.13).** `/executions` is still Ralph's "Task Loop History"; `/agent` has no heading; the dashboard's quick
   actions are four differently coloured full-width buttons.
9. **Suggestion.** The loading screen is a full-viewport primary-to-secondary gradient (the brand tokens); review it with
   the rest of the visual language in 2.13.
