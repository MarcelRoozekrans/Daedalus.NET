# Phase 1.9 — scout repository tooling

**Date:** 2026-09-19
**Status:** approved
**Surface:** Backend
**Phase:** 1.9 — Scout repository tooling
**Depends on:** 1.4 (channels), 1.5 (scheduled runs), 1.6 (diagnostics)

> **On the number.** 1.7 and 1.8 already exist and are pending, so this takes the next free label
> rather than renumbering them and churning their issue references. **It is executed before both.**
> The number is an identifier, not a position in the queue.

## Goal

Give the scout agent a way to observe repository activity, so the 7am digest contains true
statements instead of an empty report or an invented one.

## Why this exists

Phase 1.4 built the channel. Phase 1.5 built the scheduled run: a sweeper claims a due schedule and
walks it through scout → writer → deliver. Phase 1.6 built the page that says where a run died. All
three were built for one workflow — `RepoDigest`, a single disabled schedule called `daily-digest`
at cron `0 7 * * *`.

The scout's task asks for four things:

> new commits on the default branch, merged and still-open pull requests, issues opened or closed,
> and any CI runs that failed

Its configured tools are `roslyn__*`, `daedalus__*`, `memory__*`. Roslyn reads **code structure** —
types, references, call graphs. It cannot see a commit, a pull request, an issue, or a CI run.
Neither can the other two.

So the scout is instructed to report four categories it has no means of observing. That leaves two
outcomes: it reports nothing changed every morning, or it fills the gap with plausible invention.
The writer's instructions already say "never invent facts not present in the findings", which
suggests the risk was anticipated but not closed.

**Three phases of delivery machinery currently carry an empty payload.** This gap appears nowhere in
the roadmap as a phase of its own — only as carried-forward text on 1.5 and 1.6 — so following
roadmap order never reaches it.

## Decisions taken

| Decision | Choice | Why |
|---|---|---|
| Which repositories | **Any, passed as a tool argument** | The tool surface is general; the scheduled workflow's prompt is specific |
| Token capability | **Read and write** | The user wants the agent able to act, not only observe |
| Who may write | **Interactive agents only; the scout stays read-only** | The 7am run is unattended. A scout that misreads a stale PR closes it at 07:00 and a human finds out at 09:00 |
| Implementation | **Our own tool types over `HttpClient`** | The needed surface is ~8 tools; hand-writing puts the security boundary in code we test rather than in an upstream naming convention |

**On the third row.** Phase 1.6 cut `schedule__create` on the same reasoning: an agent that can
schedule work can schedule work for itself. Write capability is not wrong; handing it to an
unsupervised scheduled run is. Both halves are available here — the capability exists, and the
scout does not hold it.

**On the fourth row.** The official GitHub MCP server was the alternative and would have been
near-zero code. It was rejected on one point: **we would not control the tool names**, and the
boundary chosen above is enforced by tool-list membership. An upstream release adding a tool that
matches the scout's read pattern would silently widen it. It also wants Docker or a host binary,
where the existing `context7` server needs only `npx`.

## Architecture

Three layers.

| Component | Location | Responsibility |
|---|---|---|
| `GitHubApi` | `Daedalus.Agents/GitHub/` | Thin client over `HttpClient` — base URL, auth header, User-Agent. Knows nothing about digests |
| `IGitHubReader` | `Daedalus.Agents/GitHub/` | The five digest reads, named below |
| `IGitHubWriter` | `Daedalus.Agents/GitHub/` | Comment, label, close |
| `DaedalusRepoTools` | `Daedalus.Agents/Tools/` | `[ThalosToolType]`, injects **only** the reader |
| `DaedalusRepoActionTools` | `Daedalus.Agents/Tools/` | `[ThalosToolType]`, injects **only** the writer |

### The five reads

Named here so the implementation plan does not have to infer them from the scout's prompt:

1. **Commits on the default branch** since the window start
2. **Pull requests merged** within the window
3. **Pull requests still open**, with their age
4. **Issues opened or closed** within the window
5. **Workflow runs that concluded in failure** within the window

The default branch is not assumed to be `main` — it is read from the repository, since a caller may
name any repository.

**Two interfaces, not one.** The split is a type-level fact rather than a naming convention. A tool
class that cannot reach the writer cannot write, whatever an agent asks it to do. This mirrors
`IScheduleDiagnostics` / `IScheduleDeliveryActions` from phase 1.6, which the final whole-branch
review confirmed was genuinely enforced in DI rather than merely intended.

### The boundary is enforced three times, and the strongest is authorization

Amended after reading how Thalos actually works. The original design enforced this by tool-list
membership alone, which is what phase 1.6's final review called brittle — "enforced by tool-surface
absence, not authorization ... brittle the day a generic HTTP tool appears". That criticism applies
to the first two layers below. The third closes it.

1. **Separate tool source.** `AddLocalTools` exposes tools as `{sourceName}__{tool}`. Reads register
   under the existing `daedalus` source; writes register under a new, clearly distinct source, so
   the scout's existing `daedalus__*` glob cannot match a write tool. The names must not be one
   character apart — `daedalus_write__x` sits uncomfortably close to `daedalus__*` and is rejected
   for that reason.

2. **The agent allow-list.** `AgentDefinition.Tools` is a glob allow-list over qualified
   `source__tool` names. The scout's list omits the write source. **Note its default is
   everything** — an agent with no explicit list gets every tool, so this layer only protects agents
   that declare one.

3. **A tool policy — the real boundary.** `Thalos:ToolPolicies` binds a tool pattern to a named
   authorization policy, evaluated by `DefaultToolAuthorizer` against the caller's
   `ISecurityContext`. The precedent already exists: `roslyn__apply_*` and `roslyn__rename_*` are
   bound to `developer`, so mutating tools already require a role in this codebase.

   A scheduled run executes as `DetachedPrincipal(principalId, roles)` built from the persisted
   execution row, and the configured principal is `schedule:daedalus` with roles `["reader"]`.
   `DeveloperPolicy` requires `developer` or `admin`. **So binding the write pattern to `developer`
   denies the scheduled run at the authorizer, whatever its tool list says** — and it reuses an
   existing policy rather than inventing one.

Layer 3 is what makes this hold when someone later widens a tool list by accident. Layers 1 and 2
are defence in depth. A test covers each.

Phase 1.6's equivalent boundary held because nobody wired it wrong. This one is enforced by the
authorizer.

**Why not build on the existing GitHub code.** `GitHubPullRequestFactory`,
`RepositoryAuthenticationProvider` and `GitRepositoryManager` already exist in
`Daedalus.Infrastructure/Services/CodeAnalysis/`, and they establish the house style: raw
`HttpClient` against `api.github.com`, no Octokit. But they are Ralph-era, and **phase 1.7 retires
Ralph**. The new tools live in `Daedalus.Agents`, which 1.7 does not touch, and reuse the *idea* of
`RepositoryAuthenticationProvider` — resolve `GITHUB_TOKEN` from the environment — without
depending on the class.

## Data flow

The 7am path already works end to end and was verified in a real browser during phase 1.6. Two
things it needs do not exist yet.

### The scout must be told which repository

The tools take `owner/repo` as an argument, which makes the surface general. The scheduled digest
still needs a concrete target, and `ScoutTask` currently says "this repository" with nothing
resolving it. `RepoDigestPrompts.ScoutTask` becomes a function taking the repository, and the
schedule supplies it.

A general tool surface and a specific prompt are not in tension — but the second half has to be
built.

### The window comes from the previous occurrence, not a fixed lookback

The task says "since the last digest". The obvious implementation is a 24-hour default, and it is
subtly wrong: if the 7am run fails and the next lands at 8am, a 24-hour window silently drops an
hour of activity — on a feature whose entire purpose is telling you what happened.

The right answer is already in the data. `ScheduledRunExecution` rows carry `OccurrenceAt`, so the
previous successful occurrence is queryable and `RunSteps` passes it as `since`. A missed run
widens the next window instead of leaving a hole.

The first-ever run has no predecessor. It takes a bounded default and **states which window it
actually covered**, rather than implying completeness.

## Error handling

One principle carries this section: **an unverified category must never read as "nothing
happened."**

The digest makes five separate reads. If commits and pull requests succeed but the CI query fails,
the honest output is four categories plus "could not check CI runs" — not a silent four-category
digest that reads as a clean bill of health. This is phase 1.6's `Delivered` / `DeliveryUnknown`
distinction transplanted: absence of evidence is not evidence of absence, and the failure mode is a
7am message saying the repository is quiet when it does not know that.

| Condition | Behaviour |
|---|---|
| No token | **Fails loudly.** Never falls back to unauthenticated requests — that turns a credential problem into "that repository does not exist" on every private repo |
| 404 | Says **both** possibilities: no such repository, or the token cannot see it. From outside they are identical |
| 403 rate-limited | Distinguished from 403 forbidden by response headers, and reports **when the limit resets** |
| Result truncated | Says it was truncated. No silent caps |
| One read fails | That category reports as unchecked; the other four still report |
| Malformed `owner/repo` | Clear error, never a malformed URL |
| Any path | The token is never logged and never returned |

On the write side: failures surface rather than swallow, and **there is no automatic retry** — a
retried comment is a double comment.

## Testing

Five things carry the real risk.

1. **The boundary gets a test, not just a config line.** Read the scout's configured tool patterns;
   assert they match no tool on the write type. If someone later adds `daedalus__*` to the scout to
   fix something unrelated, the build fails rather than the 7am run quietly gaining write access.

2. **"Unverified" versus "nothing happened" gets a mutation check.** Fail one of the five reads and
   assert the output says that category could not be checked. The conflated implementation must be
   **applied and seen to fail this test**, not merely asserted to.

3. **The window, either side of a missed run.** The previous occurrence drives `since`; a skipped
   run widens the next window rather than leaving a gap.

4. **404 ambiguity, and 403 rate-limited versus forbidden.** The first covers both cases in its
   wording; the second distinguishes two responses that share a status code.

5. **Assert on the outgoing request, not only the parsed response.** These tests stub
   `HttpMessageHandler` rather than calling GitHub. A fully-stubbed test passes happily against a
   client that builds the wrong URL or omits the auth header — so at least one test captures the
   request and checks path, headers and User-Agent. Otherwise we are testing our own stub.

Plus: a missing token fails loudly, and no log line contains the token.

## Out of scope

Creating or deleting repositories, branches, or releases. Merging pull requests. Any write from the
scheduled run. GitLab, Azure DevOps and Bitbucket, which `RepositoryConfiguration` already names as
valid platforms — this phase is GitHub only, and the reader interface is not pre-generalised for
them. Retiring the Ralph-era GitHub code, which belongs to phase 1.7.

## Known narrowing

**Closed, not carried:** phase 1.6 recorded that its agent write-boundary was enforced by
tool-surface absence rather than authorization. This phase does not inherit that weakness — see
layer 3 above. The same technique would retrofit 1.6's resend endpoint, which is worth a follow-up
but is out of scope here.

**Write capability ships without a human-visible audit trail in Daedalus.** An agent comment on
GitHub is visible on GitHub, but nothing in the schedule diagnostics page records that an agent
acted. That is acceptable while only interactive agents can write — you were present when it
happened — and would need revisiting the day a scheduled run is allowed to act.
