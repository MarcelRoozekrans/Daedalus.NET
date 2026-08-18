# Phase 1.3 — Skills: agent-scoped procedure documents

**Date:** 2026-08-18 · **Milestone:** 1 (Hermes-style agent framework) · **Issue:** #229 · **Depends on:** phase 1.2
(design `docs/plans/2026-08-17-thalos-memory-design.md`, Thalos.NET 0.2.0 on nuget.org)

## 1. Goal

Give Thalos agents a library of **procedures**: named markdown documents, authored in git, that an agent can
see the titles of at all times and pull into context when one applies. "How we cut a release", "how to add an
EF migration in this repo". The model still does the work with its normal tools — a skill only supplies the
procedure.

### In scope

- `Thalos.NET.Skills` (net8.0 + net10.0): `SkillDocument` model, `ISkillStore` (records) and `ISkillIndex`
  (search) ports, `SkillFileLoader` + `SkillSyncService`, `SkillContextProvider` (the always-present
  catalogue), `skills__load` / `skills__search` tools, in-memory implementations, contract tests in
  `Thalos.NET.Testing`.
- Daedalus: `Skills` table + `PostgresSkillStore`, wiring in the API host, a `skills/` folder with two real
  starter skills, agent `Skills` globs in configuration.
- Thalos.NET **0.3.0** released, Daedalus consuming it.

### Out of scope (decisions, not omissions)

Agent-authored or agent-edited skills; versioning beyond the content hash; usage analytics; a Blazor viewer;
hot-reload (`WatchFiles`); per-skill authorization policies (globs are the only gate); skills for the Ralph
console host.

## 2. Decisions taken during brainstorming

| # | Decision | Consequence |
|---|---|---|
| S1 | A skill is a **procedure document the agent loads**, not an executable workflow or a prompt template. | No workflow engine, no execution/resumption semantics. |
| S2 | **Two-stage loading**: a compact catalogue (names + descriptions) every turn, bodies on demand via a tool. | Token cost is bounded and predictable; the agent chooses what to read. |
| S3 | **Files are the source of truth**, synced one-way into the database at startup. | Git always tells you what the agents can do; the agent cannot quietly rewrite a procedure. |
| S4 | Skills are assigned **per agent by glob** on `AgentDefinition.Skills`, mirroring `Tools`. | One familiar mechanism; an agent's capabilities are readable from its definition. |
| S5 | Search exists, but the **catalogue stays authoritative**. | `skills__search` is a convenience for large sets, not the primary path. |
| S6 | Approach **A**: a new `Thalos.NET.Skills` package with **in-process** cosine search. | Matches the real scale (a repo folder of markdown); no pgvector, no second adapter package, works on net8.0. |

### Direction note

During brainstorming the user described the capability they ultimately want: hand the agent a task ("execute
this task for the gh repo"), it works unattended, and it **asks a question when it needs a decision**. That is
the task-runner capability spread across phases 1.4–1.6, plus a mid-run clarification mechanism that does not
exist yet. The decision was to **keep 1.3 as the skills library and build the runner next**, so this design
stays deliberately small — skills are configuration an agent is given for its purpose, not a system the runner
has to interrogate at run time.

## 3. The skill file and model

`<skills-root>/<name>/SKILL.md` or `<skills-root>/<name>.md`:

```markdown
---
name: dotnet-migrations
description: How to add and apply an EF Core migration in this repo.
tags: [dotnet, ef, database]
---

# Adding a migration
1. …
```

YAML frontmatter; `name` and `description` required, `tags` optional. `name` must match the folder/file name
(a mismatch is a load error, never a silent rename) and follows the `MemoryKind` identifier rule extended to
64 chars: `^[a-z][a-z0-9_-]{0,63}$`. The body is everything after the frontmatter, **verbatim** — what the
model reads is byte-for-byte what is in git.

```csharp
public sealed record SkillDocument
{
    public required SkillName Name { get; init; }      // typed, validated identifier
    public required string Description { get; init; }  // <= 300 chars, shown in every catalogue
    public required string Body { get; init; }         // <= 64 KB
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required string SourcePath { get; init; }   // repo-relative, for error messages
    public required string ContentHash { get; init; }  // SHA-256 of the raw file
    public bool IsActive { get; init; } = true;        // false = file deleted from disk
    public required DateTimeOffset UpdatedAt { get; init; }
}
```

No `OwnerId` and no `AgentId`: unlike memories, skills are not per-user data — visibility is decided by the
agent's globs. The 64 KB cap stops one runaway file blowing a context window; over-size is a load error naming
the file.

## 4. Sync and storage

```csharp
public interface ISkillStore
{
    ValueTask<Result<SkillDocument, AgentError>> UpsertAsync(SkillDocument skill, CancellationToken ct);
    ValueTask<Result<SkillDocument, AgentError>> GetAsync(SkillName name, CancellationToken ct);
    ValueTask<Result<IReadOnlyList<SkillDocument>, AgentError>> ListAsync(SkillQuery query, CancellationToken ct);
    ValueTask<UnitResult<AgentError>> DeactivateMissingAsync(IReadOnlyList<SkillName> seen, CancellationToken ct);
}
```

`SkillQuery { Names?, Tags?, IncludeInactive }`. Thalos ships `InMemorySkillStore` and reusable
`SkillStoreContractTests`; Daedalus implements `PostgresSkillStore`.

**`SkillSyncService`** runs once at startup as an `IHostedLifecycleService.StartingAsync` — the slot the
Rag.NET schema initializer uses — so the catalogue is populated before the first turn:

1. Enumerate `SKILL.md` files under `SkillOptions.Roots` (several allowed: a repo folder plus a shared one).
2. Parse and validate. A bad file is **logged and skipped, never fatal**; the skipped count is logged at
   warning. One malformed skill must not stop the host.
3. Compare `ContentHash` — unchanged files are skipped entirely (no re-parse, no re-embed).
4. Upsert changed/new ones, re-embedding only those.
5. `DeactivateMissingAsync` marks skills whose file has disappeared inactive. They leave the catalogues but
   their rows survive, so history and references stay resolvable.

**Duplicate names across roots**: the first root wins and the second is a load error, logged with both paths.
Silent shadowing would be worse.

If the **store** is unreachable the host fails to start — unlike memory, an agent silently missing its
procedures is worse than not starting. If the **embedding generator** is absent the host starts fine (§7).

## 5. The turn

**Catalogue (always present).** `SkillContextProvider : AIContextProvider` — the hook memory already uses —
appends the agent's catalogue to its instructions for that run:

```
<skills note="procedures you may load with skills__load">
- dotnet-migrations: How to add and apply an EF Core migration in this repo.
- release: How we cut and publish a release.
</skills>
```

Only skills matching the agent's `Skills` globs, only active ones, sorted by name, capped by
`Catalogue.MaxChars` (default 2000) with an explicit `… and N more (use skills__search)` line on overflow —
truncation is never silent. An empty catalogue adds nothing. The catalogue is built once per sync and cached
per glob-set, so a turn costs a dictionary lookup rather than a query.

**Tools** — source `skills`, so `skills__load` / `skills__search`, going through `ToolCatalog` authorization
and tool events like every other tool:

- `skills__load(name)` → the body wrapped in `<skill name="…">…</skill>`, with the same `</skill`
  neutralisation `MemoryRecallBlock` applies. A name outside the agent's globs answers **"unknown skill"**,
  identical to a name that does not exist — no probing for what other agents can do.
- `skills__search(query, topK?)` → ranked `name: description` lines, never bodies, so the agent still chooses
  what to load. Without an embedding generator it returns a plain message saying search is unavailable and the
  catalogue is authoritative.

**Trust.** Skill bodies come from git, not from model output, so they are **not** passed through
`IUntrustedContentScanner` — a deliberate difference from recalled memories, documented as such: whoever can
merge a `SKILL.md` can steer the agent, which is the same trust boundary as merging code.

## 6. Daedalus integration

- `Skill` entity + EF configuration → `Skills` table (name PK, description, body, tags `text[]`, source_path,
  content_hash, is_active, updated_at); `PostgresSkillStore` passing `SkillStoreContractTests`; migration
  `AddSkills`.
- `AddDaedalusAgents`: `thalos.UseSkills(…)` bound from a new `Thalos:Skills` section (`Enabled`, `Roots`,
  `Catalogue:MaxChars`, `Search:TopK/MinScore`) plus `UseSkillStore<PostgresSkillStore>()`. **API host only** —
  the Ralph console runs no Thalos agents and gets memory only.
- A `skills/` folder with two real starter skills, so the feature ships used rather than theoretical:
  `daedalus-migrations` and `thalos-release` — both procedures executed by hand during this milestone.
- `appsettings.json`: the agent's `Skills` glob list (`["*"]` to start) and `skills__*` in its `Tools`.
- **No API or Blazor surface.** Skills are git-authored, so the repo is the UI; a viewer would be decoration.

## 7. Errors and edge cases

New `AgentErrorCode`s: `SkillNotFound`, `SkillStoreFailed`, `SkillValidationFailed`, `SkillSearchUnavailable`.
`Detail` never carries raw exception or file text (the memory policy). The catalogue provider never fails a
turn — a store error is logged, raises `SkillCatalogueFailedEvent`, and the turn proceeds without a catalogue.

- **Skill edited while the host runs**: not picked up until restart. Deliberate — changing a procedure the
  agent has already loaded mid-run would be worse. `WatchFiles` is explicitly not in 1.3.
- **Embedding generator absent or down**: sync still stores everything (embeddings are best-effort, flagged
  like memory's `IndexPending`); the catalogue works and `skills__search` reports unavailable. Skills never
  depend on Ollama being up.
- **A body larger than the remaining context**: `skills__load` returns it regardless — the model manages its
  own window. We cap at 64 KB and document it.
- **Agent with no matching skills**: no catalogue block; the tools stay registered and answer "unknown skill",
  which is simpler than removing tools per agent.

## 8. Testing

- **Thalos**: parser (valid, missing/duplicate frontmatter keys, name mismatch, over-size body, malformed
  YAML); sync (unchanged→skip via hash, changed→re-embed, deleted→deactivate, duplicate across roots, bad file
  skipped not fatal); catalogue (glob filtering, ordering, `MaxChars` overflow line); tools (out-of-glob ==
  unknown, neutralisation, search unavailable); an end-to-end turn asserting the catalogue lands in the
  instructions and `skills__load` returns the body; `SkillStoreContractTests` against the in-memory store;
  an architecture test that `Thalos.NET.Skills` depends on neither Rag.NET nor the memory package.
- **Daedalus**: `PostgresSkillStore` against the contract on Testcontainers; a migration test; a registration
  test proving the two starter skills load from disk at startup.

## 9. Delivery

Two plans, as in 1.1 and 1.2: **Plan A** (Thalos.NET: the package, tests, `pack-validate` expects nine
packages, `feat:` commits plus a `Release-As: 0.3.0` footer, publish) then **Plan B** (Daedalus: store,
migration, wiring, starter skills, consume 0.3.0).

## 10. Follow-ups recorded for later phases

- Usage counters ("which skills actually get loaded") — cheap, but wait for real usage to justify the column.
- If a corpus ever outgrows in-process search, `ISkillIndex` swaps to a pgvector implementation with no change
  above it.
- The **task runner + mid-run clarification** capability the user described (phases 1.4–1.6) is the next
  substantial piece of work; skills were kept small so they compose with it rather than pre-empting it.
