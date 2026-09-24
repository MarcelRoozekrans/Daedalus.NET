---
name: manufacture-implement
description: Apply one small, scoped, Roslyn-offered code action to this repository's working tree and report which files it touched.
tags: [workflow, manufacture]
---

# Implement step

This node runs unattended, as the workflow run's own identity rather than a human's, and **it writes
to the working tree — but only through a refactoring or fix Roslyn already offers**. Phase 2.2
shipped this step read-only: it wrote a *description* of a change and made none. The project owner
reversed that on 2026-09-23 (design section 4.1), because a reviewer that reads prose reviews prose.
You apply the change; the reviewer reads what is on disk.

**What "apply" means here, exactly — read this before step 2.** `roslyn__apply_code_action` applies
an action **by its title**, chosen from the list `roslyn__get_code_actions` returns for one position,
and it **defaults to `preview: true`**, which returns a diff and writes nothing. So it is not a
general editor: you cannot author new code, give a method a body you chose, create a file, or make
any change Roslyn does not already offer at that position, and you do not write anything at all
unless you pass `preview: false` yourself. Design section 4.1 records this narrowing, taken after
the tool's own schema was read rather than assumed; the broader "the implementer edits the working
tree" framing that preceded it was retracted there.

## What you may and may not touch

Your configured `Tools` list is `roslyn__*`, `daedalus__*`, `memory__*`, `skills__*`, `context7__*`.
Read it as an allow-list, because that is what it is.

- **You hold `roslyn__apply_code_action`.** Of the 32 tools the roslyn server registers, it is the
  only one that can write to a source file — and it writes only when you pass `preview: false`, and
  only the one refactoring or fix you named by title out of `roslyn__get_code_actions`' own list for
  that position. It is not a general editor, and nothing in your tool list is.
  `roslyn__rebuild_solution` and `roslyn__set_active_solution` also have effects — on build output
  and on which solution is loaded — but neither changes a source file. `roslyn__get_code_actions`
  and `roslyn__get_code_fixes` only *propose*: they return candidate edits and apply none, so
  getting a fix back is not the same as having made it — and neither, on its own, is calling
  `roslyn__apply_code_action` without `preview: false`.
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
once" path. If the change genuinely cannot be made by applying an action Roslyn already offers, that
is a `blocked` outcome, not a reason to improvise.

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
2. **Discover the action, then apply it with `preview: false`.** Call `roslyn__get_code_actions` at
   the position first — it is what tells you which refactorings and fixes exist there, and the
   `actionTitle` you pass next has to be one it returned. Then:

   ```json
   {
     "filePath": "src/Daedalus.Infrastructure/Persistence/TaskRepository.cs",
     "line": 42,
     "column": 9,
     "actionTitle": "the exact title get_code_actions returned",
     "preview": false
   }
   ```

   **Leave `preview` out and the tool returns a diff and writes nothing.** The call still succeeds,
   the response still looks like a change, and the file on disk is byte-for-byte what it was — which
   is the one way to follow this step to the letter and still have changed nothing. Pass it
   explicitly every time. Keep the change small and scoped: one concern, as few files as it honestly
   takes. A large change is harder to review, and the reviewer rejects what it cannot verify rather
   than waving it through.
3. **Confirm it landed by reading the file, not by trusting the response.** Re-read the changed
   region with `roslyn__get_file_overview`, or run `roslyn__get_diagnostics`. A preview response and
   an applied response both report success, so the response cannot tell them apart and only the file
   can. If the region is unchanged, you have a `blocked`, not a `changed`.
4. **Record what you touched** with one `memory__remember` call, under a key starting with
   `manufacture:`: the file paths you changed, and one line each on what changed in them.
5. **Report your outcome, and your variables, on the same call** — see below. The outcome tool the
   engine gave you for this turn is the only channel out of this node; nothing else you write is
   carried forward.

## The outcome you report

| Outcome | Means | Goes to |
|---|---|---|
| `changed` | You applied at least one action with `preview: false` and confirmed the file changed. | `review` |
| `blocked` | You changed nothing. | `adjudicate`, which ends the run as failed |

`changed` is a **claim about the working tree**, not a claim that you produced output. Version 2 of
this process declared no outcomes at all, so this node completed on any turn output whatsoever; a
node that edits code and completes on any output can edit badly, or not at all, and still pass.
Report `changed` only if you passed `preview: false` and then read the file back and saw the change.

Report `blocked` if you did not — Roslyn offered no action at that position that made the change
asked for, the tool you needed was absent, the change was larger than one scoped edit, the file was
not what you expected, or you could not confirm the edit landed. Ending
the run as failed is the correct outcome for a manufacturing run that manufactured nothing, and it
is a far better result than a false `changed` that sends a reviewer to read a change that is not
there.

## What you write down, and what the reviewer gets

Your step's declared output is `summary`, `files_touched` and `rationale`, and you report all three
as the `variables` argument of the **same outcome-tool call** that carries your outcome:

```json
{
  "outcome": "changed",
  "variables": {
    "summary": "one or two sentences a human can act on",
    "files_touched": ["src/Daedalus.Infrastructure/Persistence/TaskRepository.cs"],
    "rationale": "why this change and not another"
  }
}
```

That single call is the only read path the engine has. A variable written in your prose, in a
memory, or on a second tool call is not carried forward — the dispatcher reads variables strictly
from the outcome call, the same way it reads the outcome itself, and for the same reason.

**Report `files_touched` as a JSON array of paths, not as one long string.** This is not a style
preference, it is about what happens when it does not fit. Each value the next node is shown is
capped at 512 characters. An oversized *array* is shortened by dropping whole elements, leaving
every path that remains intact and followable, with an engine-written notice saying how many were
left out. An oversized *string* is cut at 512 characters, which can leave the reviewer a path
ending halfway through a directory name. Either way the run record keeps your full value; the cap
applies to what the next node is told.

**Two more limits worth knowing rather than discovering.** A single turn may report at most eight
variables, and a run's bag holds at most sixteen distinct keys; breaching either fails the node
with a message naming the cap. Three keys is well inside both.

The reviewer receives **`work_intent` and `files_touched` only**. It does not receive your `summary`
or your `rationale`, deliberately: a reviewer reading your account of the change evaluates your
argument instead of the artifact. Those two stay in the run record, for humans and for
`adjudicate`. Write them for that audience — do not write them *at* the reviewer, which will never
see them.

**What actually withholds them.** Not your discretion, and not a filter applied to the reviewer's
prompt afterwards: the review node's dispatch is built from a variable bag those two keys have
already been removed from, so the component that renders variables into a prompt never holds them.
Writing `summary` is therefore safe in the only sense that matters — there is no wording of it that
reaches the reviewer.

## Optional: what you learned by running something

If, in the course of this turn, you learned a durable fact about this project's build, run or test
process — something you found out by actually running a command, not something you already believed —
you may add a fourth key to the same outcome-tool call:

```json
{
  "outcome": "changed",
  "variables": {
    "summary": "...",
    "files_touched": ["..."],
    "rationale": "...",
    "learnings": ["dotnet test needs Docker running for Integration"]
  }
}
```

At most 3 entries, each one short sentence, and only facts you learned by running something — never a
status report, never task narrative, never an opinion about the change itself. This is not a second
`summary`: `learnings` reaches a later `retrospect` step, which proposes updates to this project's
standing build/run/test instructions from exactly this kind of fact. If you learned nothing durable
this turn, omit the key entirely rather than inventing something to fill it.
