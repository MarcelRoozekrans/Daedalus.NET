# UI Review: Phase 2.12 — The Manufacturing Console

**Date:** 2026-10-13
**Contract:** `docs/plans/2026-10-12-phase-2.12-manufacturing-console-ui-contract.md`
**Build:** `feat/phase-2.12-manufacturing-console` at 45db27b plus the sidebar-toggle colour fix, on the Aspire stack,
signed in through Keycloak as realm user `dev`
**Verdict:** PARTIAL

## Screenshots

Full-page captures at desktop 1920x1080, tablet 768x1024 and mobile 375x812 in
`docs/regression-screenshots/2026-10-13-1033/`, embedded per page in `docs/regression-report-2026-10-13-1033.md`. Pages in
scope: `/runs`, `/runs/23e8b6ff…` (succeeded, live-proof run 2), `/runs/e47eeac0…` (failed at the review cap,
live-proof run 1) and `/tasks`. The gate panel and the cancel dialog were not on screen at review time (no run was
parked); they were exercised live by the owner's approval of run 23e8b6ff and are covered by the browser suite
(`RunsConsoleBrowserTests`, 13 scenarios: gate body, cancel confirm, focus, live update, not found, admin-only retry).

## Contract Adherence

| Criterion | Status | Notes |
|---|---|---|
| Design system colors | ✅ Pass | `--rz-*` tokens only; no hex in `Components/Runs`, `Runs.razor`, `RunDetail.razor`. Status as badge text plus severity |
| Typography | ✅ Pass | `H4` page and run titles, `Body2` caption, `Caption` metadata, `RadzenText` throughout |
| Status mapping | ✅ Pass | Run badges per the table; steps kind-first for Failed/CapExceeded/Cancelled/Resumed/Retried (CapExceeded shows as such since c5b2d70) |
| Component coverage | ✅ Pass | RunStatusBadge, RunsGrid, RunHeader, GatePanel, CancelRunDialog, RunTimeline, ReviewEvidence, NodeUsageTable, WriteAuditTable, LiveIndicator (with Connecting) all present |
| Menu item and Tasks link | ✅ Pass | Runs item under Tasks for developer/admin with the awaiting count; Tasks badge links to its run with "Open run, <status>" |
| Filters | ⚠️ Partial | Status is a `RadzenDropDown`; Process is a `RadzenTextBox`, not a dropdown — accepted deviation R-B7-process (no process list exists; only `manufacture`) |
| Layout — desktop | ✅ Pass | `/runs` and `/runs/{id}` match the contract's structure; one content width on every page |
| Layout — tablet | ✅ Pass | Started by and updated hidden; grid scrolls inside its card; the sidebar collapses through the toggle — visible only after this review's fix (it was white on white) |
| Layout — mobile | ⚠️ Partial | Status, current node and task; tabs and tables scroll inside their card; but the opened sidebar pushes the content aside (about 100px left) instead of a single column, and the header wraps |
| Loading states | ✅ Pass | Grid `IsLoading`; run page spinner; LiveIndicator "Connecting…" until the first start |
| Empty states | ✅ Pass | "No runs yet…", "No runs match these filters.", "No steps recorded yet.", "None", "No agent turns yet.", "No writes." |
| Error, not found, forbidden | ✅ Pass | Danger alert with reason and Retry; "Run not found" with a link back; role message; signed-out visitors go to Keycloak |
| ARIA roles | ✅ Pass | Gate `region` "Approval gate"; checkboxes with `role`, `aria-checked`, `aria-label`; `aria-live="polite"` status; Reload `aria-label` |
| Keyboard navigation | ✅ Pass | Radzen defaults; Approve focused only on arrival; dialog traps focus and returns it (browser suite) |
| Live behaviour | ✅ Pass | Two live runs followed for 21 and 11 minutes with no reload (`window.__noReload` held), indicator Live |

## Anti-Slop Scan

Scope: the pages this phase built or changed.

| # | Pattern | Status | Evidence |
|---|---|---|---|
| 1 | Purple/violet gradient default | ✅ Pass | None on the console pages. (The app-wide loading screen uses the brand primary-to-secondary gradient; noted for 2.13.) |
| 2 | Generic emoji feature icons | ✅ Pass | None |
| 3 | Card with coloured left border | ✅ Pass | None |
| 4 | Hand-drawn SVG humans | ✅ Pass | None |
| 5 | Inter/Roboto/Arial as display face | ✅ Pass | Headings use the theme's display stack per MASTER.md |
| 6 | Invented metrics | ✅ Pass | Numbers shown are real token counts and times |
| 7 | Filler copy | ✅ Pass | None |
| 8 | Icon next to every heading | ✅ Pass | No heading on the console pages carries an icon |
| 9 | Gradient on every full-width background | ✅ Pass | Flat backgrounds |

## 5-Dimension Critique

| Dimension | Score (1-5) | Evidence |
|---|---|---|
| Philosophy | 3 | A recognisable operations console; the run page reads as a factory log, but the app around it is a generic admin shell |
| Hierarchy | 3 | Status badge and the gate are the focal points of the run page; the list's primary column is clear. Secondary rank flat (metadata line, all-caps badges) |
| Execution | 3 | Clean alignment and one content width; rough edges: NONE outcome badges, the browser-default PR link, all-caps CAPEXCEEDED, the GUID in "started by" |
| Specificity | 3 | Timeline with tokens and durations, gate drops, review evidence per lens are product-specific; list and header are stock Radzen |
| Restraint | 4 | No decoration; tabs keep detail out of the way |
| **Floor** | **3** | — |
| **Average** | **3.2** | — |

## Issues (prioritized)

1. ✅ Fixed during review — Major (contract, tablet/mobile layout): the sidebar toggle was invisible (white on the white
   header) at every width; `daedalus-theme.css` sets `--rz-sidebar-toggle-color: var(--rz-header-color)`; browser test
   `SidebarToggle_IsVisibleAgainstTheHeader` asserts contrast ≥ 3:1.
2. ⚠️ Mobile: the opened sidebar pushes the content instead of overlaying it; the header wraps — Major, app-wide,
   recorded for phase 2.13 (navigation and flow review).
3. ⚠️ Process filter is a text box — accepted deviation R-B7-process.
4. ⚠️ A failed run shows no reason — engine/host gap, issue #347.
5. ⚠️ Polish for 2.13: NONE outcome badges, default-styled PR link, GUID in "started by", solid vs lighter badge shades
   between Tasks and Runs.

## Verdict Rationale

PARTIAL — contract adherence has no ❌ Missing after the toggle fix, two ⚠️ Partial (process filter by accepted ruling,
the mobile sidebar push); anti-slop passes all nine; the critique floor is 3 with an average of 3.2, which is PARTIAL by
the rubric. The remaining Partials and the critique's weak points are app-wide and belong to phase 2.13, which was added to
the roadmap during this proof for exactly this work.
