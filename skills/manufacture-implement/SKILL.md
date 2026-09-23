---
name: manufacture-implement
description: Apply one small, scoped change to this repository's working tree and report which files it touched.
tags: [workflow, manufacture]
---

# Implement step

This node runs unattended, as the workflow run's own identity rather than a human's, and **it edits
code**. Phase 2.2 shipped this step read-only — it wrote a *description* of a change and made none.
The project owner reversed that on 2026-09-23 (design section 4.1), because a reviewer that reads
prose reviews prose. You apply the change; the reviewer reads what is on disk.

## What you may and may not touch

Your configured `Tools` list is `roslyn__*`, `daedalus__*`, `memory__*`, `skills__*`, `context7__*`.
Read it as an allow-list, because that is what it is.

- **You hold `roslyn__apply_code_action`.** Of the 32 tools the roslyn server registers, it is the
  only one that edits source, and it is how you make a change. `roslyn__rebuild_solution` and
  `roslyn__set_active_solution` also have effects — on build output and on which solution is loaded
  — but neither changes a source file. Note also that `roslyn__get_code_actions` and
  `roslyn__get_code_fixes` only *propose*: they return candidate edits and apply none, so getting a
  fix back is not the same as having made it.
- **You hold no `git__*` and no `repoaction__*` tool.** Not "you are denied them" — they are
  **absent from your tool list**, so no such tool is ever offered to your turn and there is nothing
  to call. You cannot branch, commit, push, tag, open a pull request, or comment on one.

That distinction is not pedantry, and getting it backwards is a defect this project has shipped
repeatedly. A *denied* tool is offered, called, and comes back as `Tool call denied: <reason>` —
you spend budget and learn something. An *absent* tool is never offered at all. Both families are
absent for you. (`Thalos:ToolPolicies` separately binds `git__*` and `repoaction__*` to the
`developer` policy, which the `workflow` role fails — a second line that would hold if the tools
were ever added to your list. It is not what holds today.)

**Do not attempt to work around this.** There is no shell, no file-write tool, and no "just this
once" path. If the change genuinely cannot be made with `roslyn__apply_code_action`, that is a
`blocked` outcome, not a reason to improvise.

## What your run leaves behind

**Nothing reverts your edits.** Not a rejected review, not the `maxVisits: 5` redirect to
`adjudicate`, not a failed run, not a run stranded by a dead-lettered dispatch. Every one of those
leaves the working tree modified, and because `review` can send the run back here up to five times,
five rounds of edits can pile up on top of each other. You cannot isolate them on a branch — you
have no git tool to make one.

So: **a failed run is not a no-op.** Deciding what to do with the changes a run leaves behind —
keep, amend, or `git checkout --` them away — is a human step, the same shape as opening the pull
request being a human step. Say what you changed clearly enough that a person can act on it without
reading your tool calls.

## Steps

1. **Read before you write.** Use `roslyn__get_file_overview`, `roslyn__find_references`,
   `roslyn__get_diagnostics` or `roslyn__search_symbols` to locate the change and understand what
   depends on it. Keep this short — your turn has a hard token budget and a deadline, and an
   exploration that exhausts them fails the node without changing anything.
2. **Apply it** with `roslyn__apply_code_action`. Keep it small and scoped: one concern, as few
   files as it honestly takes. A large change is harder to review, and the reviewer rejects what it
   cannot verify rather than waving it through.
3. **Confirm it landed.** Re-read the changed region, or run `roslyn__get_diagnostics`. A code
   action that reported success but changed nothing is the failure mode that makes `changed` a lie.
4. **Record what you touched** with one `memory__remember` call, under a key starting with
   `manufacture:`: the file paths you changed, and one line each on what changed in them.
5. **Report your outcome** through the outcome tool the engine gave you for this turn, using the
   exact value it names.

## The outcome you report

| Outcome | Means | Goes to |
|---|---|---|
| `changed` | You applied at least one edit and the tree is modified. | `review` |
| `blocked` | You changed nothing. | `adjudicate`, which ends the run as failed |

`changed` is a **claim about the working tree**, not a claim that you produced output. Version 2 of
this process declared no outcomes at all, so this node completed on any turn output whatsoever; a
node that edits code and completes on any output can edit badly, or not at all, and still pass.
Report `changed` only if you actually applied an edit.

Report `blocked` if you did not — the tool you needed was absent, the change was larger than one
scoped edit, the file was not what you expected, or you could not confirm the edit landed. Ending
the run as failed is the correct outcome for a manufacturing run that manufactured nothing, and it
is a far better result than a false `changed` that sends a reviewer to read a change that is not
there.

## What you write down, and what the reviewer gets

Your step's declared output is `summary`, `files_touched` and `rationale`.

The reviewer receives **`work_intent` and `files_touched` only**. It does not receive your `summary`
or your `rationale`, deliberately: a reviewer reading your account of the change evaluates your
argument instead of the artifact. Those two stay in the run record, for humans and for
`adjudicate`. Write them for that audience — do not write them *at* the reviewer, which will never
see them.

> **Known gap, stated rather than papered over.** Thalos 0.8.0's `WorkflowNodeDispatcher` builds
> every `NodeResult` with an empty variable bag, so no shipped code path carries a node's output
> into `WorkflowRun.Variables`. Your `files_touched` therefore does **not** reach the reviewer
> through the run's variables today; the reviewer locates the change by reading the repository.
> Wiring that handoff is later work, and this note exists so nobody reads the contract above as
> already-working plumbing.
