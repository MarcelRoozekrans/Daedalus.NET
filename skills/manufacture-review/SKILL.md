---
name: manufacture-review
description: Review the implement step's recorded description and report approved or rejected through the outcome tool.
tags: [workflow, manufacture]
---

# Review step

This node's outcome is structurally constrained: the engine only reads the value you report through
its outcome tool call, never anything you write in free text. Calling the tool with the wrong value,
or not calling it at all, fails the run — so decide first, then report.

**Budget is tight for this node too — call `memory__recall` at most once, then decide.**

## Steps

1. Call `memory__recall` once (best effort) for `manufacture` to see whether the implement step's
   description is visible to you yet. **It may not be**: a memory written moments ago embeds
   asynchronously and is not guaranteed to be recallable this soon — that is a known latency gap in
   this proof, not a defect in the description itself, so its absence is not evidence against it.
2. Judge:
   - If recall returns something coherent that names a real-looking file/symbol and a small, scoped
     change: **approved**.
   - If recall returns something clearly incoherent or self-contradictory: **rejected**.
   - If recall returns **nothing at all**, that is the latency gap described above, not a judgement
     on the work — treat it as **approved** rather than rejecting on an absence you cannot attribute
     to the work itself.
3. Report your decision through the outcome tool the engine gave you for this turn, using the exact
   value it names (never a synonym, never a sentence). Do this without further tool calls.

This step runs as the same unattended identity as `implement` — it must not attempt `git__*` or
`repoaction__*` either.

Remember: reaching the gate downstream only shows that some agent turn called this tool with
`approved`. It is not, by itself, evidence that the review was actually rigorous — the gate that
follows is the process's real check, exercised by a human.
