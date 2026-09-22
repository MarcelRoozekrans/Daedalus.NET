# Lessons for Milestone 2 from GitHub's Copilot runtime Rust port

**Source:** <https://github.blog/ai-and-ml/generative-ai/migrating-the-github-copilot-runtime-to-rust-using-copilot/>
**Recorded:** 2026-09-21, while phase 2.2 is being designed.

An 800,000-line port, 128 pull requests, 14.5 weeks, supervised by **one** engineer. The closest public
account of the thing Milestone 2 is trying to build, so it is worth mining rather than admiring.

---

## The caveat that governs everything else

**They had an oracle. We will not.**

Every verification they describe rests on behavioural equality with an existing TypeScript
implementation: end-to-end tests run against the new code, line-by-line old-versus-new comparison,
schema compatibility checks. The question "is this correct?" reduced to "does it match what we already
had?"

**A manufacturing pipeline building NEW software has no reference implementation to diff against.** The
single most load-bearing part of their process does not transfer.

That is not a reason to ignore the rest — it is a reason to be honest that our verification problem is
strictly harder than theirs, and to design for it rather than assume their gates carry over. Where they
could ask "same behaviour?", we can only ask "meets the spec?" — and the spec is prose.

---

## What transfers directly

### 1. A build-resource mutex. Implement this in 2.2 or 2.3.

> "all 15 concurrent agents on one machine each tried to build and test, and my poor laptop ground to a
> halt."

Their fix: a separate session acting as an **agentic mutex**, gating build and test execution across
eight porting sessions. Sessions denied the lock deferred and picked other work.

**We hit exactly this problem today, twice**, in a single session:

- two concurrent container-bound test suites corrupted a 24-minute measurement and produced a
  misleading count
- two agents were dispatched onto the same branch and working tree, and avoided collision only because
  they happened to touch different files

This is not speculative. It is a concrete primitive the workflow engine needs: a lease over scarce
shared resources — the build, the container suite, the working tree — with waiters deferring to other
work rather than blocking.

### 2. Work units are waves through regions, not components

> "first move the pure logic, then move state ownership, then move orchestration, then remove fallbacks"

They explicitly did **not** treat a component as the atomic unit. Median changed lines grew from 3,250
early to 99,445 late, because the unit was a *stage across a region*, not a file or a class.

Relevant to 2.2's decomposition and 2.3's routing: a work item should be a stage over a region of the
system, ordered leaves-inward, not "one task per file".

### 3. Multi-model adversarial review

Their `rust-rebase-review` skill spawned **three parallel subagents on different models** — Claude Opus
5, GPT-5.6-sol, Grok 4.6 — doing line-by-line behavioural comparison, after every rebase.

Directly relevant to **2.4's review stage**. Model diversity as a hedge against correlated blind spots
is cheap and we are not doing it: every review in phases 1.7, 1.8 and 2.1 ran on a single model family.

### 4. Failures become standing instructions

> Standing instructions evolved after each class of failure — e.g. "napi exports that do real work must
> be asynchronous and use spawn_blocking."

A failure is not just fixed; it becomes a permanent constraint on every future agent.

**Daedalus already has the machinery for this** — `daedalus__search_failure_patterns` and a failure
pattern database, built in an earlier phase. What is missing is the loop that *writes* to it when a
class of failure is found. That is a small addition with compounding value, and it belongs in 2.3 or 2.4.

Their taxonomy is worth stealing wholesale as seed data: ambiguous semantics, ambient behaviours,
incomplete paired operations, blocking the main thread, lifecycle mismatches.

### 5. "Whatever you make available is something an agent may decide applies"

Their sharpest line. Two parallel sessions collided at the most connected file in the codebase because
the kickoff language was loose — one session decided, unprompted, that merging the other's work was
within scope.

**Design consequence for 2.3's routing and 2.4's skills:** a capability offered is a capability that
will be used, in circumstances you did not picture. Constraints must be explicit and negative —
"do not touch X" — not merely absent from the brief.

This session produced the same failure in miniature: an agent was told "one dependency" as a proxy for
"no read surface", and correctly pushed back because the proxy did not fit. The rule should have been
stated as the invariant, not the proxy.

### 6. The human is a control loop, not a dispatcher

Of ~2,639 human-authored messages: **31% review, testing and CI; 17.4% challenging technical decisions;
15% pushing for completeness.** Almost none of it was assigning work.

> "push when an agent treated an intermediate stopping point as the finish line"

That is the single most common intervention in this session too — agents reporting partial completion
as done, or stalling mid-verification. It should inform 2.3's roster: the human role is review,
challenge, and insistence on completeness, and the pipeline should be built to make those three cheap.

---

## Numbers worth keeping

| | |
|---|---|
| Exploration-to-mutation ratio | **10:1** — 590,988 file views against 40,591 edits |
| `git inspect` calls | 300,530, the single most common command family, because agents constantly re-checked branch state against a moving main |
| Prompt cache hit rate | **96.22%** — they state the economics do not work without it |
| `cargo check` clean rate | 87.1% of 4,478 runs |
| Compiler error mix | 37% name/import resolution, 22% missing methods, 14% type mismatches, 11% trait bounds, **only 1.7% borrow checker** |

The error mix is the interesting one: agent failures were **mechanical, not conceptual**. That argues
for cheap fast feedback — a compiler, a fast test gate — over sophisticated reasoning about what an
agent might get wrong.

The 10:1 read-to-write ratio and the `git inspect` dominance both argue the same thing: most agent
effort is orientation. A pipeline that gives agents better orientation up front — the audit-before-
rewrite pattern that phase 1.8 used to good effect — should pay for itself.

---

## What they rejected, and why it matters to us

**Parallel shadowing.** They explicitly declined to maintain TypeScript and Rust versions side by side:
the coupling that makes a component hard to port also makes dual maintenance near impossible, risking
"more regressions than confidence gained."

Worth holding against **phase 2.5**, Ralph retirement. The current plan keeps Ralph running until
2.1-2.4 demonstrably do its job — which is a shadowing strategy. Their experience suggests the dual-run
period should be as short as we can make it, not indefinite.
