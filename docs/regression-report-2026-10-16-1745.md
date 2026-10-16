# Regression Report: Phase 2.14, Flux across the Web app

| Metric | Value |
|---|---|
| Date | 2026-10-16 17:45 UTC (sweep 17:15–17:45) |
| Application URL | http://localhost:5290, the Aspire stack, branch `phase/2.14-flux` at 8da0d7ce |
| Signed in as | realm users `dev` (developer, task-manager, project-manager, analyst) and `admin` through Keycloak |
| Pages tested | 13: `/` signed out and signed in, `/tasks`, `/runs`, `/runs/{id}` with a real run and not found, `/projects`, `/prd-generator`, `/agent`, `/schedules`, `/costs`, `/repositories` as admin and as developer, `/brainstorm/{id}`. Also the Create task and Create project dialogs |
| Viewports tested | 3: desktop 1920x1080, tablet 768x1024, mobile 375x812 |
| Existing tests | Not run in this part; Task 10 part 1 covers the suites. This part is the live sweep only |
| Console errors found | 0 unexpected. Expected: the 404 for an unknown run or brainstorm session, the 403 on `/repositories` as developer, the 400s of the PRD and brainstorm model calls |
| Network errors found | 0 from the Web app. The PRD and brainstorm 400s come from the model call, see N1 |
| Regressions against 2.13 | **0** |
| New findings | 5, none caused by this phase: 1 Major, 1 Major outside the Web, 3 Minor |
| Overall status | PASS for the phase; WARN for the app because of N1 and N2 |

Baseline: `docs/regression-report-2026-10-15-2026.md` and `docs/plans/2026-10-15-ui-review-phase-2.13.md`, screenshots
in `docs/plans/ui-review-2026-10-15-phase-2.13/`. Intended deviations: `docs/plans/2026-10-16-phase-2.14-rulings.md`,
"Visible deviations".

## Browser

As in 2.13, the Playwright MCP server cannot start, because it wants Google Chrome. The sweep drove the Playwright 1.64
Node library with the cached `chromium_headless_shell-1248`, with `LD_LIBRARY_PATH` and `FONTCONFIG_FILE` from
`~/.cache/daedalus-btest`. Passwords were read from `keycloak-realm.json` at run time and are not recorded anywhere.

## Visual comparison with 2.13

Every page was captured at the three widths and diffed pixel by pixel against the 2.13 capture of the same name. Each
page was also measured for `scrollWidth` against `clientWidth`. **No page overflows sideways at any width**, the run
page included.

Every difference traces to one of three causes, and none is a 2.14 change:

- **Data.** 2.13 had a project, `UI review 2.13`, and two tasks; this database started empty. PRD Generator therefore
  shows "No projects found", Tasks and Projects show their empty states, and the Schedules next run is a day later.
- **2.13's own review fixes,** which landed after the 2.13 sweep. These are the footer text and version, the centred
  signed-out header, Tasks' "New task" and "No tasks yet." empty state, the one-column phone grid, the Agent page
  stacked on a phone, the "Page not found" page, the named task-dialog fields, and the repositories alert for a
  non-admin. Each is listed under "The 2.13 findings".
- **Nothing else.** Home, Runs, Costs, Schedules and the run not-found page differ by under 1% at desktop, which is
  anti-aliasing and the clock. The phone diffs are larger only where a 2.13 fix changed the layout.

The intended 2.14 deviations seen live: Home's cards update on a run change (live check 6), and the first expanded
Schedules row stays open (live check 2). The PRD R8 fix could not be seen, because generation fails before the review
step (live check 3). The brainstorm persistence fix could not be seen either, because no session can be opened (live
check 4). The others are timing or renewal paths, which a sweep cannot drive.

| Page | Desktop | Tablet | Mobile | Result |
|---|---|---|---|---|
| `/` signed out | ![](plans/regression-2026-10-16-phase-2.14/landing-signed-out-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/landing-signed-out-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/landing-signed-out-mobile.png) | Pass. Sign in is centred: y 13–41 in the 56px header |
| `/` signed in, empty | ![](plans/regression-2026-10-16-phase-2.14/home-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/home-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/home-mobile.png) | Pass; identical to 2.13 but for the footer |
| `/` with failed runs | ![](plans/regression-2026-10-16-phase-2.14/home-with-runs-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/home-with-runs-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/home-with-runs-mobile.png) | Pass; see N4 for the second row's name |
| `/tasks`, empty | ![](plans/regression-2026-10-16-phase-2.14/tasks-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/tasks-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/tasks-mobile.png) | Pass. "No tasks yet." with New task |
| `/tasks` with a task | ![](plans/regression-2026-10-16-phase-2.14/tasks-with-task-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/tasks-with-task-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/tasks-with-task-mobile.png) | Pass. At 375 the row stacks: title, status and PR, then the actions |
| Create task dialog | ![](plans/regression-2026-10-16-phase-2.14/task-create-dialog-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/task-create-dialog-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/task-create-dialog-mobile.png) | Pass. The fields are named Project, Title, Description, Priority, Complexity and Prompt. With no project, "No projects yet. Create a project" |
| `/runs`, empty | ![](plans/regression-2026-10-16-phase-2.14/runs-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/runs-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/runs-mobile.png) | Pass |
| `/runs` with runs | ![](plans/regression-2026-10-16-phase-2.14/runs-with-run-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/runs-with-run-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/runs-with-run-mobile.png) | Pass |
| `/runs/{id}`, a real failed run | ![](plans/regression-2026-10-16-phase-2.14/run-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/run-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/run-mobile.png) | Pass. First time seen live: the Failed pill, "started by dev", and the failure reason "Implement reported it is blocked." The timeline has its usage line |
| `/runs/{id}` not found | ![](plans/regression-2026-10-16-phase-2.14/run-not-found-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/run-not-found-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/run-not-found-mobile.png) | Pass |
| `/projects` | ![](plans/regression-2026-10-16-phase-2.14/projects-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/projects-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/projects-mobile.png) | Pass; the Create project dialog is N3 |
| `/prd-generator` | ![](plans/regression-2026-10-16-phase-2.14/prd-generator-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/prd-generator-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/prd-generator-mobile.png) | Pass; generation is N1 |
| `/agent` | ![](plans/regression-2026-10-16-phase-2.14/agent-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/agent-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/agent-mobile.png) | Pass. The sessions sit above the chat at 375 |
| `/schedules` | ![](plans/regression-2026-10-16-phase-2.14/schedules-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/schedules-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/schedules-mobile.png) | Pass |
| `/costs` | ![](plans/regression-2026-10-16-phase-2.14/costs-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/costs-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/costs-mobile.png) | Pass |
| `/repositories`, admin | ![](plans/regression-2026-10-16-phase-2.14/repositories-admin-desktop.png) | ![](plans/regression-2026-10-16-phase-2.14/repositories-admin-tablet.png) | ![](plans/regression-2026-10-16-phase-2.14/repositories-admin-mobile.png) | Pass. The "Admin" divider and Git Repositories are in the menu; the empty state offers Add repository |

## Live checks deferred to this sweep

1. **Costs: pass, with one part not exercisable.**
   - The KPI cards read 0, $0.00, 0 and 0 on the empty database. After the sweep's agent turns and two manufacture
     runs they read 378.9K tokens, $0.01, 137.9K cache-read tokens and 9 entries; the By Source, By Model and By Day
     tables agree (`costs-with-data-desktop.png`). Chat tokens are unpriced, as the page's own note says.
   - The estimator changed from Claude Haiku 4.5, at $0.07 to $0.24, to Claude Opus 4, at $1.08 to $3.60, for 10 turns
     of 4000 tokens (`costs-estimator-desktop.png`).
   - Not exercisable: expanding a row. The only expandable grid is "Task Loop History by Project", which holds
     iterations of the task loop retired in 2.8, and this database has none.
2. **Schedules: pass.**
   - The list shows `daily-digest`, Disabled. Expanding it shows "Run history — daily-digest" with "No runs yet.",
     and the row is still open 5 seconds later: the Task 1 fix holds (`schedules-row-expanded-desktop.png`).
   - `/schedules?scheduleId=1a9ce179-…` opens with that row expanded and focus on its "Run history" heading
     (`schedules-deep-link-desktop.png`).
   - Resend could not be checked: the schedule has no delivery, failed or otherwise, so no Resend button exists.
3. **PRD generator: blocked at generate by the model, N1.** The project step and the requirements step work. Generate
   PRD answers 400 at once and shows the alert "LLM invocation failed: Error invoking Claude: Status Code: NotFound
   {"type":"error","error":{"type":"not_found_error","message":"model: claude-sonnet-4-20250514"},"request_id":"…"}"
   (`prd-after-generate-desktop.png`). This is the API's error string, as ruling 11 intends, not raw JSON from the Web.
   Review, select and convert could not be reached.
4. **Brainstorm: blocked at session creation by the same model, N1.** `POST /api/brainstorm/sessions` answers 400 with
   the same `not_found_error` for `claude-sonnet-4-20250514`. No session exists, so sending a message, keeping it
   across a reload and advancing a phase could not be checked. The Web app also has no way to open a session: the page
   is reachable only at `/brainstorm/{id}`. An unknown id shows the phase chips, an empty transcript and a composer;
   the failure toast is gone before 4s (`brainstorm-unknown-session-desktop.png`). That is the same as on main.
5. **Agent: pass for the Web, with a server-side finding, N2.**
   - A short turn works: "SESSION A READY", with `claude-sonnet-5 · 29848 in / 9 out`.
   - **Streaming.** A long essay was asked for. The page showed "Thinking…" and an empty Assistant card for about 24s
     (`agent-streaming-desktop.png`), then about 4,100 characters arrived at once. That is not a Web fault. Measured
     on the wire with `fetch` from the page, the API sends ": connected" at 2ms, then all 278 `text-delta` events in a
     single 19.5KB chunk at 14,969ms, then usage and done 24ms later. The Web renders what it is given.
   - **Stop: pass.** Stop pressed while thinking shows the alert "Cancelled. The turn was stopped." and gives back
     Send (`agent-after-stop-desktop.png`). Nothing arrived in the 35s after. The stopped turn is not kept: the
     session lists 0 turns and returns to Idle.
   - **Switching sessions mid-turn: pass.** A turn was sent in session C. Four seconds in, session A was opened. A
     showed only its own transcript, with no text from C's turn, then or 40s later
     (`agent-switched-mid-turn-desktop.png`). The composer was enabled, and C's turn was stopped, as
     `OpenSessionAsync` says.
   - Because of N2, Stop could not be pressed while text was arriving. It was pressed only during the wait.
6. **Home live cards: pass.**
   - The sweep made a project on the allow-listed sandbox repository (`daedalus-sandbox`) and a task in it.
   - With Home open in one tab, Manufacture was pressed in a second. Without a reload or navigation, Home's Running
     card went to "Running (1) … at implement" at 6s (`home-live-1-desktop.png`).
   - At 27s, Running went back to 0 and Failed showed "(1) … Implement reported it is blocked. failed less than a
     minute ago" (`home-live-2-desktop.png`).
   - A second Manufacture opened the new run page 3.06s after the click, when the 201 arrived.
7. **Git Repositories: pass.** As admin, the list shows with the Admin menu group at every width. As dev, there is no
   menu entry, and `/repositories` shows the alert "Git repositories are managed by admins." in place of an empty list
   or an Add button (`repositories-dev-desktop.png`). The 403 shows only in the console.

## The 2.13 findings

| 2.13 finding | Status in 2.14 |
|---|---|
| 1 Task dialog fields unnamed, Project named by a GUID | **Fixed and holds**: named Project, Title, Description, Priority, Complexity, Prompt |
| 1 (same item) The Projects dialog has the same defect | **Still present**: Project Name, Description and Version have no label link and no `aria-label` (`finding-project-create-dialog-desktop.png`). Not fixed in 2.13 and not filed; see N3 |
| 2 Text contrast below 4.5:1 | **Captions fixed and hold**: `.page-caption` is `rgb(97,97,97)`, about 5.9:1. The rest is filed: white on primary and success as #374, faded labels elsewhere as #375; both are open |
| 3 `/agent` two columns at 375 | **Fixed and holds**: the sessions are stacked above the chat |
| 4 `/repositories` as non-admin shows an empty list | **Fixed and holds**: the admin-only alert |
| 5 Tasks' empty grid says "No records to display." | **Fixed and holds**: "No tasks yet." with New task |
| 6 Signed-out header not centred | **Fixed and holds** |
| 7 Session-ended notice shown about 0.1s | Not re-tested: it needs the 300s token expiry. Ruling 10 of 2.14 names the notice's 1.5s |
| 8 Content paints about 55ms before the sidebar | Not re-tested |
| 9 Wording: New task and Create Task; Create wraps at 375; footer; `?state=` after logout; bare not-found | **Fixed and hold**. Both pages say "New task", and Create Project fits on one line at 375. The footer reads "A person plans work…". Log out lands on `http://localhost:5290/` with no query. Unknown paths show "Page not found" with Go to Home |
| 10 Tasks at 375 hides Status and Manufacture | **Fixed and holds**: one stacked column |
| 11 Accepted deviation R-B7-process | Unchanged |

## New findings

None is caused by 2.14: each is in code that this phase did not change, or in the API.

- **N1. Major. PRD generation and brainstorm sessions fail on a retired model.** Page: `/prd-generator` at every width;
  `POST /api/brainstorm/sessions`. Steps: choose a project, enter requirements, then Generate PRD. Evidence:
  `prd-after-generate-desktop.png`, a 400 with Anthropic's `not_found_error` for `claude-sonnet-4-20250514`. Source:
  `ClaudeConfiguration.DefaultModel` in `src/Daedalus.Infrastructure/Configuration/ClaudeConfiguration.cs:11`. The same
  id is the Costs estimator's default, `CostsState.cs:13`. The Agent, on Thalos's `claude-sonnet-5`, works. No open
  issue covers it. The alert also shows the provider's raw error body and request id, which is #326's pattern.
- **N2. Major, API or Thalos side. Agent turns do not stream.** Page: `/agent`. Steps: send a prompt that needs a long
  answer. Evidence: the wire timing in live check 5. All `text-delta` events leave the API in one burst when the model
  finishes, so the user waits about 15–25s on "Thinking…", and Stop can only cancel before any text. The controller
  disables buffering and flushes the first frame, so the hold is upstream, in `RunTurnStreamingAsync` or the provider
  client. No open issue covers it. #383 is a different Agent stream issue.
- **N3. Minor, carried from 2.13 and unfiled. The Create and Edit project dialogs' fields have no accessible name.**
  Page: `/projects`, Create project, every width. Evidence: the three inputs have random ids, labels with an empty
  `for`, and no `aria-label`; `getByRole('textbox', {name: /project name/i})` finds 0. The Edit dialog has the
  same markup. The 2.13 review named it, and 2.13 fixed only the task dialogs, through `TaskFields.razor`.
- **N4. Minor, API side. An older run of a re-manufactured task loses its task name.** Page: `/`, the Failed card.
  Steps: manufacture a task, let it fail, then manufacture it again. Evidence:
  `home-with-runs-desktop.png`. The newer run reads "Regression 2.14: add a one-line comment to the README"; the older
  reads "manufacture run aed728de". `WorkflowRunListItem.TaskTitle` is null for a run that is no longer the task's
  current run. `NeedsMe.Name` falls back correctly.
- **N5. Minor. With a session open, the Agent page is 78px taller than the body at 1920x1080.** The composer's lower
  part and its Send button sit under the footer until the body is scrolled (`agent-session-c-reopened-desktop.png`;
  `.rz-body` scrollHeight 1064 against clientHeight 986). `/brainstorm/{id}` shows the same thing. `Agent.razor.css`
  and `MainLayout` are unchanged in this phase, so this predates 2.14. It likely dates from 2.13's two-line footer
  text, but that is unverified.

## Data created and cleanup

- Deleted through the UI: the projects `Regression 2.14 scratch`, made in the UI, and `Regression 2.14 sandbox`, made
  through the API because the UI cannot set a repository URL. Also the task `Regression 2.14: add a one-line comment
  to the README`.
- Left, because the UI cannot delete them:
  - two failed manufacture runs, `aed728de-9224-4f7c-9f55-f1c9893a942d` and `ba426968-e101-4a46-9ddc-668f796afd43`,
    both against `daedalus-sandbox`, both blocked at implement, with no PR;
  - seven `dev` Agent sessions of the Daedalus Architect, made between 19:32 and 19:38 local time;
  - nine cost entries.
- No brainstorm session, PRD or schedule was created.

## Recommendations

1. **N1:** move the PRD and brainstorm model off `claude-sonnet-4-20250514`, and the Costs estimator's default with
   it. Two of the seven live checks cannot pass until this is fixed.
2. **N2:** find where the Agent turn's deltas are held back, in Thalos's streaming runtime or the provider client.
3. **N3:** name the project dialog fields as `TaskFields.razor` does.
4. **N4, N5:** small; the first is in the API's run list and the second in the full-height page layout.
