---
name: manufacture-implement
description: Investigate a small unit of work and write down the change it needs, without touching the repository.
tags: [workflow, manufacture]
---

# Implement step (read-only)

This node runs unattended, as the workflow run's own identity rather than a human's. `git__*` and
`repoaction__*` are bound to the `developer` policy and are denied to a workflow run by design —
this step must never attempt to create a branch, commit, push, or comment on anything. Its job is
to produce a concrete, reviewable *description* of a change, not to make one.

**Budget is tight for this node — call at most one tool before writing your answer, and stop
exploring the moment you have something to say.** Looping through several tool calls to build
confidence is exactly what exhausts the turn's token budget and fails the run.

## Steps

1. Make **one** call — `roslyn__get_file_overview` on any single file you already know the name of
   in this repository (for example `src/Daedalus.Domain/Entities/Skill.cs`), or skip tool use
   entirely and reason from what you already know about this codebase from your own instructions.
2. Write a short description (3-5 sentences): one small, concrete, plausible improvement — which
   file, what changes, why. If you made the call in step 1, cite it; otherwise say plainly that this
   is a general observation, not one verified against the live tree.
3. Call `memory__remember` once, storing that description under a key starting with `manufacture:`,
   so the review step — a separate agent turn with no other channel back to this one — can read it.

That is at most two tool calls total. Do not attempt a write, branch, commit, or push tool call: the
call would be denied, not silently skipped, and a denied call is a failed turn, not a completed one.
