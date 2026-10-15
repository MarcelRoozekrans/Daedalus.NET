# Design System

**Product type:** Admin / back-office — an internal operator console
**Tech stack:** Blazor WebAssembly + Radzen.Blazor
**Generated:** 2026-09-18
**Direction:** none applied — see below

> **This file documents a design system that already exists in code. It does not invent one.**
>
> `ui-design-system` says not to generate over "an existing design system already encoded in code",
> and Daedalus has one: a 236-line theme at `src/Daedalus.Web/wwwroot/css/daedalus-theme.css`
> defining the full Radzen token set, applied consistently across 27 `.razor` files. Every value below
> was read out of that file or observed in the existing pages. Nothing here is a fresh palette, and a
> new page that ignored this would be a regression however good it looked alone.
>
> **The source of truth is the CSS.** If the two ever disagree, the CSS wins and this file is stale.

## Color Palette

All tokens are Radzen CSS custom properties. Reference them as `var(--rz-primary)` — never hard-code
a hex in a component.

### Brand

| Token | Value | Notes |
|---|---|---|
| `--rz-primary` | `#667eea` | indigo-periwinkle |
| `--rz-primary-light` | `#8b9ef0` | |
| `--rz-primary-lighter` | `rgba(102, 126, 234, 0.12)` | tints and hover fills |
| `--rz-primary-dark` | `#4f64d4` | |
| `--rz-primary-darker` | `#3d4fba` | |
| `--rz-secondary` | `#764ba2` | purple |
| `--rz-secondary-light` | `#9570b8` | |
| `--rz-secondary-lighter` | `rgba(118, 75, 162, 0.12)` | |
| `--rz-secondary-dark` | `#5e3a82` | |
| `--rz-secondary-darker` | `#482c64` | |

### Semantic

Each also has `-light`, `-lighter`, `-dark` and `-darker` variants following the same pattern.

| Token | Value | Meaning in this product |
|---|---|---|
| `--rz-success` | `#10b981` | completed, healthy, delivered |
| `--rz-danger` | `#ef4444` | failed, error, undelivered |
| `--rz-warning` | `#f59e0b` | degraded, stuck, overdue — needs attention but not broken |
| `--rz-info` | `#3b82f6` | in progress, informational, not yet due |

### Neutrals and surfaces

| Token | Value |
|---|---|
| `--rz-body-background-color` | `#f8f9fc` |
| `--rz-base-background-color` | `#ffffff` |
| `--rz-text-color` | `#334155` |
| `--rz-text-title-color` | `#1e293b` |

### Elevation

| Token | Value |
|---|---|
| `--rz-card-shadow` | `0 1px 3px rgba(0,0,0,.06), 0 1px 2px rgba(0,0,0,.04)` |
| `--rz-header-shadow` | `0 1px 3px rgba(0,0,0,.08)` |

Shadows are deliberately near-invisible. Depth here comes from surface and border, not from drop
shadows.

## Typography

**No font family is declared anywhere in the project** — not in `index.html`, not in the theme, not in
`app.css`. The app inherits Radzen's default stack, and headings are shaped with `TextStyle` on
`RadzenText` rather than CSS.

That is a real, current fact rather than an omission in this document. **Do not introduce a webfont as
part of a feature phase** — it is a product-wide change that would alter every existing page, and it
deserves its own decision.

Type is expressed through Radzen's `TextStyle` enum — `H5`, `Body1`, `Caption` and so on — observed in
`MainLayout.razor` and across the pages. Use those rather than inline `font-size`.

## Spacing and Radius

| Token | Value | Notes |
|---|---|---|
| `--rz-border-radius` | `8px` | the only radius declared; applies to cards, inputs, buttons |

Spacing uses Radzen's `RadzenStack` with an explicit `Gap` — `Gap="0.5rem"` and `Gap="0"` both appear
in `MainLayout.razor`. Layout padding is set inline in `rem` where needed. There is no custom spacing
token scale, and introducing one would be a product-wide change, not a phase-level one.

## Component Patterns

These are the conventions the existing pages already follow. New pages match them.

### Chrome

`RadzenLayout` → `RadzenHeader` + `RadzenSidebar` + body. The sidebar is a dark navy panel
(`linear-gradient(180deg, #1a1f36, #1e2440)`), 260px wide, collapsible via `RadzenSidebarToggle`,
holding `RadzenPanelMenu` groups separated by `.sidebar-section-divider` captions such as
"Operations". A new page adds a `RadzenPanelMenuItem` with a Material icon name.

### Data-heavy pages

`Runs.razor` is the reference implementation, with its grid in `Components/Runs/RunsGrid.razor`. The established shape:

- The shared page header: an `H4` title and a `Body2` caption, with no icon beside the title. The caption carries the
  theme's `page-caption` class, `--rz-text-secondary-color`, which reaches 4.5:1 on the body and on a card; never an
  `opacity`, which fades it below that
- `RadzenCard` to group a grid and its controls, one card for the filters and one for the grid
- `RadzenDataGrid` with `AllowSorting="true"`; a grid wider than a phone scrolls inside its card, never the page, and
  drops its less important columns below 1280px and 768px through `RadzenMediaQuery`
- Paging with Previous and Next buttons under the grid when the API pages by cursor, as runs do
- `RadzenBadge` for status, carrying the semantic colour and its text, through `RunStatusBadge`
- `RadzenAlert` for error and empty states, scoped to the card that failed
- `RadzenStack` for arrangement; `RadzenText` for all copy
- Filter controls as `RadzenDropDown` / `RadzenTextBox` inside a `RadzenFormField`

Text drawn in or on a semantic colour uses its `-darker` shade, Radzen's `Shade.Darker`: the Manufacture button's
white label on `--rz-success-darker`, and the outlined Priority badges' text. The default shades read below 4.5:1 with
white or on white. The primary colour under white text is the open exception, #374.

### Status → severity mapping

Status is communicated by badge colour plus text, never colour alone. For phase 1.6's verdicts:

| Verdict | Severity | Token |
|---|---|---|
| `Disabled` | Light / muted | neutral |
| `Delivered` | Success | `--rz-success` |
| `Running` | Info | `--rz-info` |
| `NotYetDue` | Light / muted | neutral |
| `Overdue` | Warning | `--rz-warning` |
| `Stranded` | Warning | `--rz-warning` |
| `DeliveryUnknown` | Warning | `--rz-warning` |
| `Failed` | Danger | `--rz-danger` |
| `Undelivered` | Danger | `--rz-danger` |

`Undelivered` is danger rather than warning on purpose: the run succeeded, so every other signal on
the page says healthy, and the delivery failure is the one thing the operator must not scan past.

For a manufacture run, phase 2.12's console maps a run status and a timeline step. A page that shows either calls
`RunStatusLabels` (`Daedalus.Web/Services/RunStatusLabels.cs`) and never writes a new mapping.

| Run status | Badge text | Severity |
|---|---|---|
| `Running` | Running | Info |
| `Awaiting`, signal `human_approval` | Awaiting approval | Warning |
| `Awaiting`, any other signal | Waiting: `<signal>` | Warning |
| `Succeeded` | Succeeded | Success |
| `Failed` | Failed | Danger |
| `Cancelled` | Cancelled | Light / muted |

| Timeline event kind or outcome | Badge text | Severity |
|---|---|---|
| outcome `approved`, `published` (except the five kinds below) | the outcome | Success |
| outcome `rejected`; kind `Failed`, `CapExceeded` | the outcome, or for those two kinds the kind | Danger |
| kind `Resumed`, `Retried` | the kind, whatever the outcome, plus "by `<actor>`" as caption text | Info |
| outcome `none` (except the five kinds below) | no badge | — |
| anything else | the outcome, else the kind | Light / muted |

A step of kind `Failed`, `CapExceeded`, `Cancelled`, `Resumed` or `Retried` always shows its kind, whatever outcome it
holds; a cancelled run's step has kind `Cancelled`: text `Cancelled`, Light. `RunStatusLabels.ShowsStepBadge` decides
whether a step has a badge at all.

A review visit's verdict badge, on the run's Review tab, is a solid pill whose text is the verdict; its severity comes
from `RunStatusLabels.VerdictStyle`:

| Review verdict | Badge text | Severity |
|---|---|---|
| `Approved` | Approved | Success |
| `Rejected` | Rejected | Danger |
| `Unreadable`, a record of the visit could not be read | Unreadable | Warning, never Success |

## Stack-Specific Notes

- **Radzen, not MudBlazor.** The generic Blazor guidance recommends MudBlazor; this project chose
  Radzen and has standardised on it. Do not mix libraries.
- Theming is CSS custom properties in `daedalus-theme.css`, **not** a C# theme provider object.
- Blazor WebAssembly. `Daedalus.Web` references only `Daedalus.Application`, so any type a page binds
  to must live in `Daedalus.Application/DTOs/`.
- The API is reached over `HttpClient` with an OIDC bearer token attached; there is a test-mode path
  that skips token attachment.
- Per-component styling goes in a scoped `Component.razor.css`, following `MainLayout.razor.css`.

## Anti-Patterns

The general list, narrowed to what actually threatens this codebase:

1. **No new gradients.** The theme has exactly one, on the sidebar, and no page adds its own; Home's
   stat-card gradients went in phase 2.13. Gradients are one moment per page at most, never a page
   background.
2. **No hard-coded hex values in components.** Every colour is a `--rz-*` token. A literal hex is how
   a theme change silently stops applying to one page.
3. **No colour-only status.** Every badge carries text as well as severity.
4. **No icon beside every heading.** A heading is already a heading. Icons belong in the sidebar and
   on actions.
5. **No invented metrics or filler copy** — no "10× faster", no "Feature One", no lorem ipsum reaching
   a contract or an implementation.
6. **No second component library.** Radzen is the choice; Bootstrap or Material components alongside
   it produce two visual languages in one app.
7. **No webfont introduced by a feature phase.** See Typography — it is a product-wide decision.

## Known Gaps

Recorded honestly rather than papered over, so a later phase can decide deliberately:

- **No declared font stack.** The app relies on Radzen's default.
- **No spacing token scale.** Gaps are set per-use in `rem`.
- **No dark mode.** The sidebar is dark; the app is otherwise light-only, with no theme toggle.
- **No documented type scale.** `TextStyle` is used consistently, but the mapping from style to size
  lives in Radzen rather than in this project.
