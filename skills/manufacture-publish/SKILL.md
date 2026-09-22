---
name: manufacture-publish
description: Record what a human just approved for publication, without opening any real pull request.
tags: [workflow, manufacture]
---

# Publish step

By the time this node runs, a human has already resumed the run's approval gate — that is what let
the run reach this node at all. This step's job is to write down what was approved, not to publish
it. This node runs as the same unattended identity as `implement` and `review` — it must not attempt
`git__*` or `repoaction__*`. Both are bound to the `developer` policy and denied to a workflow run by
design (see `manufacture-implement`'s remarks); the tools being present in this agent's configured
toolset does not change that.

**Budget is tight — at most two tool calls before you report: one recall, one remember. Do not loop.**

## Steps

1. Call `memory__recall` once for keys starting with `manufacture:` and find the description the
   implement/review steps left.
2. Call `memory__remember` once to store one final record — key `manufacture:published:<a short
   slug>` — summarising what was approved and that a human gate cleared it. This is the audit trail;
   nothing downstream reads it automatically.
3. Report through the outcome tool the engine gave you for this turn, using the exact value it names
   (`published`). This is the only thing that structurally proves this step ran to completion — it
   does not prove the record you wrote is accurate, or that anyone will ever read it.

Opening a real pull request against a real repository for this change is a separate, deliberately
manual step for a human to take outside this run — not something this node does or should attempt.
