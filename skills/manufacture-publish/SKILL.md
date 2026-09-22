---
name: manufacture-publish
description: Record what a human just approved for publication, without opening any real pull request.
tags: [workflow, manufacture]
---

# Publish step

By the time this node runs, a human has already resumed the run's approval gate — that is what let
the run reach this node at all. This step's job is to write down what was approved, not to publish
it: this agent has no `git__*` or `repoaction__*` tools in its toolset, and a workflow run could not
use them even if it did (see `manufacture-implement`'s remarks on the `developer` policy boundary).

**Budget is tight — at most two tool calls: one recall, one remember. Do not loop.**

## Steps

1. Call `memory__recall` once for keys starting with `manufacture:` and find the description the
   implement/review steps left.
2. Call `memory__remember` once to store one final record — key `manufacture:published:<a short
   slug>` — summarising what was approved and that a human gate cleared it. This is the audit trail;
   nothing downstream reads it automatically.

Opening a real pull request against a real repository for this change is a separate, deliberately
manual step for a human to take outside this run — not something this node does or should attempt.
