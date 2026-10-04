---
name: manufacture-implement
description: Make one small, scoped change in this run's worktree through workspace__*, build and test it, report the files.
tags: [workflow, manufacture]
---

# Implement step

This node runs unattended, as the workflow run's own identity rather than a human's, and **it edits
this run's own worktree**. Every run gets its own git worktree of the repository it was started on,
on its own branch, `manufacture/<run-id>`. Nothing you write reaches another run, the repository's
default branch or any remote while you work. You make the change; the reviewer reads what is on
disk; host code commits and publishes it after a human approves.

## Your run mode

**Your task states the mode this run executes in**, in a section headed `## Run mode` whose line reads
`Run mode: sandbox` or `Run mode: local`. The host wrote it when the run started, from its own
configuration. Every rule below that differs by mode follows that stated run mode. Do not work the
mode out from your tool list: the stated run mode is the authority, and a long tool list is easy to
misread.

- **Sandbox mode** (`Run mode: sandbox`): the worktree lives in this run's own container. Any file
  extension is writable except the protected paths below, and you build and test the change with
  `sandbox__build` and `sandbox__test`.
- **Local mode** (`Run mode: local`): the worktree is the host's own. Only `.cs` and `.md` files are
  writable, and you hold no `sandbox__*` tool, so you cannot build or test.

**If the stated mode and your tools disagree, do not guess.** If the task states sandbox mode but
`sandbox__build` or `sandbox__test` is not in your tool list, or it states local mode but you hold
either one, or it states no run mode at all, report `blocked` with a `summary` naming the
discrepancy, for example "Run mode: sandbox is stated, but sandbox__test is not in my tool list".

## What you may and may not touch

Your configured `Tools` list holds named Roslyn read tools plus `roslyn__apply_code_action`, and
`daedalus__*`, `memory__*`, `skills__*`, `context7__*`, `workspace__*` and, in sandbox mode, the two
sandbox tools `sandbox__build` and `sandbox__test`. In local mode skip every step below that names
them, and the rules marked "local mode" apply. The Roslyn entries are
exact names with **no glob**: a Roslyn tool that is not named in your list is not offered to your
turn, whatever the server exposes. Operator tools such as loading, rebuilding or trusting a solution,
and the background-task tools, are not in it either, so you cannot call them; the run's solution is
already loaded for you. Read the list as an allow-list, because that is what it is.

- **You edit files with the four workspace tools.** `workspace__read_file` reads a file and
  `workspace__list_files` lists a directory. `workspace__write_file` creates or replaces a whole file.
  `workspace__edit_file` replaces an exact piece of text with another, and the text you name must
  occur **exactly once** in the file: read the file first and quote enough of it to be unique.
- **Paths are relative to the repository root.** `..` and absolute paths are refused, and so are
  the protected paths below. `AGENT.md` holds this project's standing instructions; a change to it is
  proposed by a later step and applied only by a human, so if you learned something durable, report
  it as `learnings` (below) instead of writing it there.
- **Protected paths are not yours to change.** These are protected: `.git/`, `AGENT.md`,
  `.gitattributes`, `.gitmodules`, `.github/`, `.gitlab-ci.yml`, `azure-pipelines.yml`,
  `.azure-pipelines/`, `.circleci/` and `Jenkinsfile`. A trailing `/` protects the whole directory. The
  list is fixed by the host and no configuration removes an entry. In both modes the write tools refuse
  these paths, so a run that needs one is blocked. In a sandbox, publish also refuses them again whatever
  the sandbox allowed, and it refuses a `.gitattributes` or a `.gitmodules` at any depth, so
  `sub/.gitattributes` is as refused as the root one, and symlinks and git submodule pointers wherever
  they are. In local mode only `.cs` and `.md` files are writable, so none of those can be written.
  Do not create any of them. If the work needs one, do not work around it, for example by moving the
  change into a file it does not belong in: report `blocked`, naming the file and the change it needs,
  so a human can make it.
- **In a sandbox any other file extension is writable**, project files and config files included.
- **Local mode narrows that further.** Only `.cs` and `.md` files are writable there, and project
  files, props, targets, `.json`, `.yml` and files with no extension
  are refused. The protected paths still apply on top, so `.github/x.md` is not writable in local
  mode either. The same `blocked` report applies, naming the file and the change.
- **Use the named Roslyn read tools to understand the code, and `roslyn__get_diagnostics` after
  editing.** The Roslyn server you reach is this run's own, over the same worktree.
  `roslyn__get_code_actions` lists the refactorings and fixes Roslyn offers at a position, and
  `roslyn__apply_code_action` applies one by its title. That tool **defaults to `preview: true`**,
  which returns a diff and writes nothing: pass `preview: false` when you mean it to write, then read
  the file back to confirm.
- **You hold no `git__*` and no `repoaction__*` tool.** Not "you are denied them": they are **absent
  from your tool list**, so no such tool is ever offered to your turn and there is nothing to call.
  You cannot commit, push, tag, open a pull request or comment on one. Host code commits the
  worktree, pushes the run's branch and opens the pull request, and only after a human approves at
  the gate.

That last distinction is not pedantry, and getting it backwards is a defect this project has
shipped repeatedly. A *denied* tool is offered, called, and comes back as `Tool call denied:
<reason>`: you spend budget and learn something. An *absent* tool is never offered at all.

**Do not attempt to work around any of this.** There is no shell in either mode. The sandbox tools
take no command of yours. There is no "just this once" path: a write the workspace tools refuse is a
`blocked` outcome, not a reason to improvise.

## What your run leaves behind

**Nothing reverts your edits inside a run.** A rejected review sends the run back here, over the
same worktree, and with `maxVisits: 5` up to five rounds of edits can pile up on top of each other.
Build on what the earlier round left rather than assuming a clean tree.

So: **a failed run is not a no-op.** A run that ends failed keeps its worktree, with every edit in
it, for a human to inspect. Deciding what to do with it is a human step. Say what you changed
clearly enough that a person can act on it without reading your tool calls.

## Steps

1. **Read before you write.** Use `workspace__list_files`, `workspace__read_file`,
   `roslyn__get_file_overview`, `roslyn__find_references` or `roslyn__search_symbols` to locate the
   change and understand what depends on it. Keep this short: your turn has a hard token budget and
   a deadline, and an exploration that exhausts them fails the node without changing anything.
2. **Make the change.** Prefer `workspace__edit_file` for a change inside an existing file, and
   `workspace__write_file` for a new file. Keep the change small and scoped: one concern, as few
   files as it honestly takes. A large change is harder to review, and the reviewer rejects what it
   cannot verify rather than waving it through.
3. **Confirm it landed, and that it compiles.** Re-read the changed region with
   `workspace__read_file`, and run `roslyn__get_diagnostics` on the files you touched. A tool response
   is not the file; only the file is. Fix any error your change introduced before you report.
   In sandbox mode, also run `sandbox__build` and `sandbox__test`, as below.
4. **Record what you touched** with one `memory__remember` call, under a key starting with
   `manufacture:`: the file paths you changed, and one line each on what changed in them.
5. **Report your outcome, and your variables, on the same call.** See below. The outcome tool the
   engine gave you for this turn is the only channel out of this node; nothing else you write is
   carried forward.

## Building and testing in the sandbox

In sandbox mode, run `sandbox__build` and then `sandbox__test` after your edits and before you
report `changed`. Each takes no command: it runs a fixed command in a throwaway copy of this run's
worktree inside the sandbox, so a build or a test never changes the files the reviewer will read.
Read the exit code and the summary in each result. A non-zero exit code is a failure, and so is a
summary that reports failed tests or build errors.

- If either fails, fix the cause in the worktree with the workspace tools and run it again. Do
  not claim `changed` while the build or the tests fail.
- If you cannot make them pass, report `blocked` with the failure as `summary`.
- Each tool runs plain `dotnet build` or `dotnet test` with no `--no-restore`, so it restores inside its
  throwaway copy, and the only network it reaches is NuGet. A `PackageReference` you add from nuget.org
  restores at build time; a package from any other source fails the build.
- `roslyn__get_diagnostics` runs on the real worktree, which was restored once when the sandbox started
  and not again. After a project file change it can report a missing package or type that
  `sandbox__build` resolves. **`sandbox__build` is the authority on whether the change builds**: when
  they disagree, trust the build.
- Mention the result in `summary`, for example the test count, and put a durable fact you learned
  from running them in `learnings`.

In local mode you hold neither tool and cannot build or test; `roslyn__get_diagnostics` is your
compile check and the claim you make is only that the edit landed.

## The outcome you report

| Outcome | Means | Goes to |
|---|---|---|
| `changed` | You edited the worktree and read the change back. | `review` |
| `blocked` | You changed nothing that answers the work. | `adjudicate`, which ends the run as failed |

`changed` is a **claim about the worktree**, not a claim that you produced output. Report it only if
you wrote the change and then read the file back and saw it. In sandbox mode, it also means the last
`sandbox__build` and `sandbox__test` you ran passed.

Report `blocked`, with the reason as `summary`, if you did not: the work needs a protected path or, in local
mode, a file you may not write, the change was larger than one scoped edit, the code was not what the work
described, you could not confirm the edit landed, or the stated run mode and your tools disagree. Ending
the run as failed is the correct outcome for a
manufacturing run that manufactured nothing, and it is a far better result than a false `changed` that
sends a reviewer to read a change that is not there.

## What you write down, and what the reviewer gets

Report `summary` and `files_touched`, and optionally `rationale` and `learnings`, as the `variables`
argument of the **same outcome-tool call** that carries your outcome:

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
memory, or on a second tool call is not carried forward: the dispatcher reads variables strictly
from the outcome call, the same way it reads the outcome itself.

**`summary` goes into the pull request.** When a human approves the run, host code opens the pull
request and its body carries your `summary` as the implementer's account of the change. Only this
node may write `summary`: a value any other node reports under that name is removed before it is
stored. Write it for the human who reviews the pull request.

**Report `files_touched` as a JSON array of paths, not as one long string.** Each value the next node
is shown is capped at 512 characters. An oversized *array* is shortened by dropping whole elements,
leaving every path that remains intact and followable, with an engine-written notice saying how many
were left out. An oversized *string* is cut at 512 characters, which can leave the reviewer a path
ending halfway through a directory name. Either way the run record keeps your full value.

**Two more limits worth knowing rather than discovering.** A single turn may report at most eight
variables, and a run's bag holds at most sixteen distinct keys; breaching either fails the node
with a message naming the cap. Four keys is well inside both.

The reviewer receives **`work_intent`, `files_touched` and the host's `run_mode` only**. It does not receive your `summary`
or your `rationale`, deliberately: a reviewer reading your account of the change evaluates your
argument instead of the artifact. It reads the worktree itself.

**What actually withholds them.** Not your discretion, and not a filter applied to the reviewer's
prompt afterwards: the review node's dispatch is built from a variable bag those two keys have
already been removed from, so the component that renders variables into a prompt never holds them.

## Optional: what you learned by running something

If, in the course of this turn, you learned a durable fact about this project's build, run or test
process, something you found out by actually running a command rather than something you already
believed, you may add `learnings` to the same outcome-tool call:

```json
{
  "outcome": "changed",
  "variables": {
    "summary": "...",
    "files_touched": ["..."],
    "learnings": ["dotnet test needs Docker running for Integration"]
  }
}
```

At most 3 entries, each one short sentence, and only facts you learned by running something: never a
status report, never task narrative, never an opinion about the change itself. `learnings` reaches a
later `retrospect` step, which proposes updates to this project's standing instructions from exactly
this kind of fact. If you learned nothing durable this turn, omit the key entirely rather than
inventing something to fill it.
