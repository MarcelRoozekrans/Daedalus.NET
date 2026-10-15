# UI Review: Phase 2.13 — Navigation and flow

**Date:** 2026-10-15
**Contract:** `docs/plans/2026-10-15-phase-2.13-navigation-and-flow-ui-contract.md`, with its two amendments: the
Unreadable review verdict, and the Tasks grid showing Created rather than Updated (#373)
**Design system:** `docs/design/MASTER.md`; `src/Daedalus.Web/wwwroot/css/daedalus-theme.css` wins where they differ
**Build:** `phase/2.13-navigation-flow` at cbdcd997, on the Aspire stack: Web :5290, API :5010, Keycloak :8082
**Signed in as:** realm users `dev` (developer, non-admin) and `admin`, through Keycloak
**Verdict:** PARTIAL

## Screenshots

Full-page captures in `docs/plans/ui-review-2026-10-15-phase-2.13/`, named `{page}-{desktop,tablet,mobile}.png`.
Widths: desktop 1920x1080, tablet 768x1024, mobile 375x812. The sweep report,
`docs/regression-report-2026-10-15-2026.md`, embeds them per page.

Pages in scope: `/` signed in and signed out, `/tasks`, `/runs`, `/runs/{id}`, `/projects`, `/prd-generator`,
`/agent`, `/schedules`, `/costs` and `/repositories` as admin. Also the Create task dialog at three widths, the Edit
task dialog, the phone menu overlay and user menu, and the loading screen.

**Browser.** The Playwright MCP server could not start. It asks for the `chrome` channel at
`/opt/google/chrome/chrome`, which is not installed, so the extracted Chromium libraries could not help it. The review
drove the same Playwright 1.64 library from Node instead, with the cached `chromium_headless_shell-1248` and the
extracted libraries and `fonts.conf` from the scratchpad. The full `chromium-1208` build also fails, because it needs
`libcups.so.2`.

**No run exists on this stack.** The database is fresh: no runs, tasks or projects. `/runs/{id}` was reviewed only in its
not-found state. `RunHeader` and `ReviewVisits` were checked against the contract in code and through the browser suite:
`A_failed_run_shows_its_reason`, `The_review_tab_lists_every_visit_with_findings_and_the_answer` and the
`RunDetail_*` scenarios. Starting a real run needs a configured repository, a sandbox and LLM turns, so it was not
cheap. Instead the review created a project with no repository, `UI review 2.13`, and two tasks in it. That exercised
Create, and Create and manufacture with a refused start. The whole browser suite passed on this build, 147 of 147, as did
Unit, Integration and Playwright.Api; the sweep report has the counts.

## Contract Adherence

| Criterion | Status | Notes |
|---|---|---|
| Design system colors | ✅ Pass | `--rz-*` tokens only. No hex in components: the one grep hit is a comment in `VerdictBadge.razor.css`. The one gradient is the sidebar's |
| Typography | ✅ Pass | Every page has an `H4` title with a `Body2` caption and no icon beside the title, Agent included. Measured on every page at every width |
| Status mapping | ✅ Pass | `RunStatusBadge` is a solid pill on Tasks and Runs. `ShowsStepBadge` hides `none` outcomes. Verdict badges come from `RunStatusLabels.VerdictStyle`, so Unreadable is Warning |
| `SignedOutLanding` | ⚠️ Partial | Product name, the README sentence and one primary Sign in, with no menu, toggle or gradient, at all three widths. But the header's logo and Sign in sit at the top edge of the 56px header instead of being centred, at every width (`landing-signed-out-*.png`) |
| `NeedsMePanel` | ✅ Pass | Four cards in the contracted order, `H6` titles and counts, muted empty lines, one New task button and no quick actions. Two columns on desktop, one on tablet. The cards with data are covered by `Home_*` browser tests |
| `TaskCreateDialog` | ⚠️ Partial | Field order, defaults, a filterable project with its repository or "No repository" as secondary text, preselection, focus on Project, Prompt under a collapsed Advanced, errors on submit, Create, and a refused Create and manufacture all match. The refusal closes onto Tasks with "The task was created, but manufacture was refused: The project has no repository URL." But the fields have no accessible names; see ARIA roles |
| `TaskEditDialog` | ✅ Pass | The same fields and Prompt fallback, with Save changes and no manufacture button (`task-edit-dialog-desktop.png`) |
| `TasksGrid` | ⚠️ Partial | Columns Title, Project, Status, Pull request, Priority, Created and Actions, with Prompt and Iterations gone. Manufacture has its visible label and the accessible name "Manufacture". Priority and Created are hidden at 768. Empty grid says "No records to display.": see Empty states |
| `RunHeader` | ✅ Pass (code and tests) | `RunStarter.Name` with an "unknown" fallback. The PR link is a `RadzenLink` with primary colour and `open_in_new`. The Failed alert shows `failureReason`, or "The run failed; the reason is in the timeline." Not seen live: no run exists |
| `ReviewVisits` | ✅ Pass (code and tests) | Newest first; verdict pill and "rejected by"; lens toggles are buttons with `aria-expanded`/`aria-controls`; "How implement answered" / "No response recorded"; "No review has run yet."; an unreadable record shows an alert. Not seen live |
| `MainLayout` | ✅ Pass | The menu groups match the contract and Executions is gone. Git Repositories shows for `admin` only, under an "Admin" divider. Signed out, there is no sidebar and no toggle. Below 768 the menu overlays the page with a backdrop. It closes on navigation, on a backdrop tap and on Escape. Focus is trapped in the menu and returns to the toggle. The header is compact, with a "User menu" icon menu holding Dev User and Log out |
| `PrdGenerator` | ✅ Pass | `scrollWidth` equals the viewport at 375, 768 and 1920. In 2.12 it overflowed by 197px |
| Agent page header | ✅ Pass | `H4` "Agent" with a `Body2` caption |
| Loading screen | ✅ Pass | Body background, "Daedalus" and a `--rz-primary` progress ring with `role="progressbar"`. No gradient (`loading-screen-desktop.png`) |
| Executions removed | ✅ Pass | No menu item. `/executions` shows the router's "Sorry, there's nothing at this address." |
| Layout — desktop | ✅ Pass | 260px sidebar open, one content column, Home 2x2 |
| Layout — tablet | ✅ Pass | Sidebar starts closed. Home's cards stack in contract order. Tasks hides Priority and Created, and the grid scrolls inside its box |
| Layout — mobile | ⚠️ Partial | No page overflows at 375. Overlay menu and compact header work. But `/agent` keeps two columns at 375: the session list and a roughly 70px empty-state column that wraps one word per line (`agent-mobile.png`) |
| Loading states | ✅ Pass | Loading screen, then "Checking authentication…". Grids use `IsLoading` and Home's cards have skeletons in code |
| Empty states | ⚠️ Partial | Home, Runs, Projects and Repositories each have a muted sentence. Tasks shows Radzen's default "No records to display.", with no "No tasks yet. New task" (`TasksGrid` has no `EmptyText`) |
| Error states | ⚠️ Partial | Run not found shows a Warning alert with "Back to Runs". But `/repositories` opened by a developer gets a 403 and shows "No Repositories Configured" with an Add repository button. A refused read looks like an empty list (`finding-repositories-as-developer-desktop.png`) |
| Session expired | ✅ Pass | The token expired after the Keycloak session ended. The Info notification "Your session has ended. Signing in again." appeared, then the redirect to Keycloak, then back to `/tasks`. No Error toast, no console error. The notification is on screen for about 0.1s before navigation, so nobody can read it |
| Signed out, data page | ✅ Pass | `/tasks` goes to `/authentication/login`, then Keycloak, then `login-callback`, then `/tasks` |
| ARIA roles | ⚠️ Partial | The menu has `role="navigation"` and the label "Main". Toggle "Menu", "User menu" and Manufacture are named. But in Create and Edit task, `RadzenFormField` labels have an empty `for`. Title, Description and Prompt have no accessible name; Project's is a GUID (`aria-label="ff762c45-…"`); Priority and Complexity read "1". The Projects dialog has the same defect, which is older than this phase |
| Keyboard navigation | ✅ Pass | Focus trap cycles the menu. Escape closes it and focus returns to the toggle. The dialog opens on Project |
| Color contrast | ⚠️ Partial | The contract asks for 4.5:1 on text. Page captions are `Body2` at `opacity: 0.6`, which gives 3.29:1 on every page, the new Landing and Home included. The Manufacture label is white on `--rz-success`, 2.54:1. Primary button text is white on `--rz-primary`, 3.66:1. Priority badge text is 3.68:1. The "Operations"/"Admin" dividers are 2.61:1 on the navy sidebar |

## Anti-Slop Scan

Scope: the pages this phase built or changed, plus the app shell.

| # | Pattern | Status | Evidence |
|---|---|---|---|
| 1 | Purple/violet gradient default | ✅ Pass | The 2.12 loading-screen gradient is gone. The only gradient is the navy sidebar |
| 2 | Generic emoji feature icons | ✅ Pass | No emoji in any `.razor` file |
| 3 | Card with coloured left border | ✅ Pass | None |
| 4 | Hand-drawn SVG humans | ✅ Pass | None. Landing is text only |
| 5 | Inter/Roboto/Arial as display face | ✅ Pass | No font declared; Radzen's default stack for all text, per MASTER.md's recorded gap. No display face was introduced |
| 6 | Invented metrics | ✅ Pass | Counts and costs are real; Home's counts come from the API |
| 7 | Filler copy | ✅ Pass | None |
| 8 | Icon next to every heading | ✅ Pass | Page titles have none. Home's quick-actions icon is gone. Only PRD Generator's "Select Project" card heading has one, well under 60% |
| 9 | Gradient on every full-width background | ✅ Pass | Flat backgrounds |

## 5-Dimension Critique

| Dimension | Score (1-5) | Evidence |
|---|---|---|
| Philosophy | 3 | Home now says "what needs you, most urgent first" and the menu follows the path. But the footer still reads "Autonomous Code Analysis Platform" and Keycloak says "AI-powered code analysis", an older identity than the one the app now has |
| Hierarchy | 3 | Home has one focal point, New task, and urgency-ordered cards. On Tasks, every row carries a solid green Manufacture and a solid red Delete, so the row's action column outweighs the title and status |
| Execution | 3 | Clean grid and one content width. Rough edges: the signed-out header sits at the top edge; "Create Task" wraps to two lines at 375 next to a two-line caption. On cold load, the page renders about 55ms before the sidebar, then shifts right by 260px. Captions are at 3.3:1 |
| Specificity | 3 | Product-specific: the needs-me cards, the refused-manufacture message, the failure reason, review visits with implement's answer. Generic: the landing sentence describes a mechanism, not a product, and the not-found page is the router default |
| Restraint | 4 | Executions, quick actions, the loading gradient and four Ralph-era fields are gone. Nothing decorative was added |
| **Floor** | **3** | — |
| **Average** | **3.2** | — |

## Issues (prioritized)

1. ⚠️ **Major (contract, accessibility).** Task dialog fields have no accessible names. Project's name is a GUID, and
   Priority and Complexity read "1". Page: `/tasks`, Create and Edit task, every width. Steps: open Create task, then
   query the accessibility tree; `getByRole('textbox', {name: 'Title'})` finds 0. Evidence: `<label>` `for=""` on
   every `RadzenFormField`; input `id="Title"` exists but nothing points at it.
2. ⚠️ **Major (contract, contrast).** Text below 4.5:1: page captions (3.29:1, `opacity: 0.6` in 11 files), the
   Manufacture label (2.54:1, white on `--rz-success`), primary buttons (3.66:1), Priority badges (3.68:1) and sidebar
   dividers (2.61:1). Pages: all, every width. The primary and success colours are tokens, so part of the fix is a
   token decision.
3. ⚠️ **Major (contract, mobile layout).** `/agent` is two columns at 375. The empty-state column is about 70px and
   wraps one word per line. Evidence: `agent-mobile.png`.
4. ⚠️ **Major (contract, error state).** A developer who opens `/repositories` gets a 403, which shows as "No
   Repositories Configured" with an Add repository button. A refusal reads as an empty list, which the spec's
   anti-goal forbids. Steps: sign in as `dev`, then open `/repositories`. Evidence:
   `finding-repositories-as-developer-desktop.png`, API `GET /api/repositories` 403.
5. ⚠️ **Minor (contract, empty state).** An empty Tasks grid says "No records to display." rather than "No tasks yet."
   with the New task action. Steps: a fresh database, then `/tasks` (seen before the review's tasks were created).
6. ⚠️ **Minor (contract, landing).** The signed-out header's logo and Sign in sit at y≈0–28 of the 56px header instead
   of centred, at all widths. Evidence: `landing-signed-out-desktop.png`, `landing-signed-out-mobile.png`.
7. ⚠️ **Minor (critique, execution).** The session-ended notification is on screen for about 0.1s before the redirect,
   so it is never read. Steps: sign in, end the Keycloak session, wait out the 300s access token.
8. ⚠️ **Minor (critique, execution).** On a cold load, signed in, the page content paints about 55ms before the
   header's user and the sidebar. The content then moves 260px right on desktop. Sign in never flashes.
9. ⚠️ **Minor (critique, specificity and consistency).** The action is "New task" on Home but "Create Task" on Tasks.
   At 375, "Create Task" and "Create Project" wrap to two lines. The footer says "Autonomous Code Analysis Platform".
   After Log out the URL keeps `?state=…`. `/executions` and other unknown paths show the router's bare "Sorry,
   there's nothing at this address."
10. ⚠️ **Minor (critique, mobile).** The Tasks grid at 375 shows Title, which is truncated, and Project. Status and
    Manufacture are only reachable by scrolling the grid sideways. The contract allows the grid to scroll, but the
    columns that matter are the ones hidden.
11. ⚠️ **Accepted deviation, carried from 2.12.** Runs' Process filter is still a text box, under ruling R-B7-process.

## Verdict Rationale

PARTIAL. Contract adherence has no ❌ Missing and eight ⚠️ Partial: landing header, task dialog, Tasks grid, mobile
layout, empty state, error state, ARIA and contrast. Anti-slop passes all nine patterns. The critique floor is 3 and
the average 3.2, which is PARTIAL by the rubric. The path the phase set out to fix works end to end in the browser:
sign in, create a task with three fields, a plain refusal, Manufacture labelled, the run and session handling, a usable
phone shell. What remains is accessibility, three page-level states and polish. Issues 1 to 4 are the ones to fix
before calling the phase done.
