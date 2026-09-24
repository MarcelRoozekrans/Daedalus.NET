# Parked ideas

Ideas raised and deliberately deferred, kept here so the reasoning behind them is not rediscovered
from scratch. Nothing here is committed work.

---

## Ariadne.NET — chatbot product built on Rag.NET

**Raised:** 2026-09-21. **Status:** parked — revisit as a NEW APPLICATION, not a Daedalus milestone.

The idea: build and deploy chatbots for customers, using Rag.NET.

### The name

**Ariadne.NET.** Verified free on nuget.org on 2026-09-21; bare `ariadne` is taken, with 3 versions at
0.1.4, but the `.NET` suffix is both unclaimed and the house convention — `Thalos.NET`, `Rag.NET`,
`Ariadne.NET`.

The myth carries the product. Daedalus built the Labyrinth; Ariadne's thread is how you find your way
through it. A retrieval-augmented chatbot is exactly that — a thread through a body of knowledge to the
one answer someone needs. It is the only candidate considered where the name explains the product to
anyone who knows the story, and it ties to Daedalus specifically rather than being generically Greek.

Sub-packages would read naturally in the established shape: `Ariadne.NET.Ingestion`,
`Ariadne.NET.Channels`, `Ariadne.NET.Tenancy`.

Rejected: `Pythia` and `Mentor` are free but belong to different myths and carry no tie to Daedalus;
`Iris` is taken; `Oracle` is unusable.

**Worth reserving now.** NuGet IDs are first-come, and a placeholder 0.0.1 costs nothing. That is how
bare `ariadne` was lost. Not done yet — publishing is outward-facing and is the user's call.

### Why it is not a Daedalus milestone

Daedalus is an agent framework for *software work*. Its domain is tasks, projects, executions,
scheduled runs and repositories. A customer chatbot platform has a different domain entirely —
tenants, bots, knowledge sources, conversations, usage and billing. Forcing it into Daedalus's schema
and bounded contexts would distort both, so the natural shape is a separate application that consumes
the two existing frameworks rather than a phase inside this one.

### The finding that matters: Rag.NET is far larger than Daedalus uses

**Daedalus consumes exactly 2 of roughly 75 Rag.NET projects** — `Rag.NET.Abstractions` and
`Rag.NET.VectorStores.PgVector`, pulled transitively through `Thalos.NET.Memory.RagNet` 0.5.1. That is
the vector store for agent memory and nothing else.

What Rag.NET already ships and Daedalus touches none of:

| Category | Count | Examples |
|---|---|---|
| Data providers | 19 | GitHub, Slack, Notion, Confluence, Jira, Google Drive, Dropbox, Box, Zendesk, Gmail, Microsoft365, Linear, Asana, Airtable, Azure Blob, Bitbucket, GitLab, Web |
| Parsers | 9 | PDF including Azure Document Intelligence, Office, HTML, Email, EPUB, Audio, Vision, Archive |
| Vector stores | 7 | PgVector, Qdrant, Pinecone, Weaviate, Chroma, Redis, Azure AI Search |
| Advanced RAG | 5 | Raptor, Raptor.Store, GraphRag, Graph, QueryTechniques |
| API and hosting | 7 | REST, gRPC, both clients, Hosting, CLI, MCP |
| Quality | 4 | Evaluation, Evaluation.Ragas, Benchmarks.Quality, Diagnostics |
| Security | 3 | Security, Security.AspNetCore, Security.Audit.Sqlite |
| Chunking and reranking | 5 | Chunking, Chunking.CSharp, Chunking.Templates, Reranking.Cohere, Reranking.Onnx |

### What this implies for scope

A customer chatbot product is **mostly assembly, not construction**:

- **Rag.NET already covers** ingestion, parsing, chunking, retrieval, reranking, evaluation, hosting,
  security and MCP.
- **Daedalus and Thalos.NET already cover** the channel layer via `IChannelAdapter` with Telegram, CLI
  and HTTP, the agent runtime, subagents, and agent-scoped skills.
- **What genuinely does not exist** is the product layer: multi-tenancy and isolation, per-customer bot
  provisioning and configuration, binding knowledge sources per tenant, and deployment.

So the open questions when this is picked up are about the product, not the RAG: is one deployment one
bot or many; what does deploy mean concretely — a container per bot, or a hosted multi-tenant service;
and where does customer knowledge come from.

### Before starting

Rag.NET itself was not inspected beyond its project list. Whether those 75 projects are mature,
published and usable as-is is unverified — the same "a name is not a contract" trap that cost four
corrections during phase 1.7. Check before planning around them.

---

## Shared configuration repository — skills, processes and role charters

**Raised:** 2026-09-23, during the phase 2.3 brainstorm. **Status:** parked — revisit **after phase
2.4**, as its own phase in Milestone 2 or a Milestone 3 entry.

The idea: move `skills/`, `processes/` and eventually role definitions out of this repo into a
separate one, so configuration can be shared between consumers instead of being copied.

### Why it is cheap later rather than urgent now

The extension points already exist and each has exactly one implementation:

- `IProcessDefinitionSource` — phase 2.2. The single implementation is
  `FileSystemProcessDefinitionSource`, renamed from `GitProcessDefinitionSource` during Task 11
  precisely because it read a directory and the old name promised git. The name is free for this.
- `ISkillStore` — phase 1.3, markdown procedure documents.

So this is a second implementation behind two existing ports, not a restructuring.

### Why after 2.4 specifically

Phase 2.4 turns the manufacturing steps into skills rather than code. That phase decides what a
process and a role *are* as content. Moving the content to a shared repo before its shape settles
means migrating it twice.

### The part that must be designed, not discovered

A shared config repo turns process definitions into a **supply chain**. Today an unattended agent
executes a process file that can only land via a reviewed pull request to this repository. Pull from
a second repo and a change there alters what an unattended agent does — which is the exact class of
risk `git__*` and `repoaction__*` sit behind the `developer` policy to prevent.

Phase 2.2 already built half the answer: content-hash immutability plus version pinning mean a
running workflow cannot have its graph swapped mid-flight, and a same-version content change is
refused outright. The missing half is **provenance** — which repository, which commit, and who was
permitted to push it. A shared repo without that is a way for an unreviewed change to reach an agent
that holds write tools.

### Related

Phase 2.3's roster decision keeps agents in `appsettings.json` rather than markdown charters for
exactly the adjacent reason: charter-as-markdown belongs to 2.4, and a shared repo belongs after it.

---

## A second chat provider — Microsoft.Extensions.AI is already the abstraction

**Raised:** 2026-09-23, during phase 2.3 execution. **Status:** parked — revisit **after phase 2.4**,
as its own phase.

The idea as raised: Thalos has a direct Anthropic implementation, and the preference is
Microsoft-first, matching the other AI packages in this estate.

### The premise is already satisfied, which is why this is cheap rather than large

Verified in the Thalos repo rather than assumed:

- `Microsoft.Extensions.AI` 10.10.0 is a **direct dependency of `Thalos.NET` core**, and
  `IChatClient` is the abstraction the framework runs on.
- `IChatClientProvider` is one method — `IChatClient CreateChatClient(AgentDefinition agent)` — and
  its own XML doc already names the intent: *"Anthropic, OpenAI, a fake…"*.
- `Thalos.NET.Anthropic` is **three files**. Its `AnthropicChatClientProvider` calls
  `.AsIChatClient(...)` and hands back the standard interface.
- The `Anthropic` SDK package is referenced **only** by `Thalos.NET.Anthropic`. Core never sees it.

So this is not a re-architecture away from a direct Anthropic dependency. The architecture is
already provider-agnostic; there is currently one provider. Adding a second is a new small package
implementing one method.

### Why it matters beyond tidiness

Phase 2.3's decision D12 puts the reviewer on a peer-strength model from a **different family**, so
it does not share the implementer's blind spots and therefore does not reproduce the same reasoning
errors when judging them.

Today both roles resolve through the Anthropic provider, so "different family" can only mean a
different Anthropic model — same lineage, and therefore some shared blind spots. A second provider
is what makes D12 literally true rather than approximately true.

### Why after 2.4 rather than inside 2.3

Phase 2.3's value is the isolation: the reviewer cannot read the implementer's reasoning and cannot
edit the code it judges. That holds regardless of which provider serves either role. Adding a
provider mid-phase widens the diff, introduces a second credential path, and adds a second failure
mode to an end-to-end proof that is already the phase's most expensive step — without making the
isolation any stronger.

### What the work would have to cover

- A provider package implementing `IChatClientProvider`, plus its options and builder extension,
  mirroring `Thalos.NET.Anthropic`'s three-file shape.
- Credential handling for the new backend, and what happens when only one provider is configured.
- **Pricing entries for the new provider's models.** Phase 2.3's `Every_model_configured_on_an_agent_has_a_price`
  drift guard already enforces this: configure an agent on a model with no price and the test goes
  red. That guard was written for the squad's second model and will do this job unchanged.
- Whether `AgentDefinition` needs to name a provider, or whether the model id alone is enough to
  route — the current `IChatClientProvider` resolves one provider per host, not per agent, so
  per-agent provider selection is the real design question hiding in this idea.

That last point is the only genuinely open question here, and it is the reason this deserves a phase
rather than a patch.

---

## Jev as a pre-LLM tool and skill selector

**Raised:** 2026-09-24, during phase 2.4. **Status:** parked, **conditional**. Revisit only once the
implement-turn token bloat has been measured and tool schemas turn out to dominate it.

Jev is TypeSafe AI's "System One" model: a hosted classifier, not a chat model. Input text plus a map of
predefined questions goes in, and one typed, probability-weighted answer per question comes out. It has
three primitives: Noul, a yes/no probability; Choice, one of up to 255 fixed options; and Score, 2 to 10
ordered levels. It cannot generate text or call tools. It is called over HTTP with `POST /v1/systemone`.
No official .NET SDK was found on 2026-09-24. The vendor claims up to 200x faster and 400x cheaper than
an LLM on classification, and that is **unverified**.

### The one place it could earn its keep

Phase 2.3 carried forward item 2: one `implement` turn used **358703 input tokens** against a
`MaxTotalTokens` of 150000. The 32 roslyn tool schemas and the skill catalogue land in every prompt. The
published .NET pattern is exactly this: score each tool or skill against the request before the chat
call and expose only the winners.

### Why it is not the next step

- **The bloat is unmeasured.** Nobody has broken the 358k into schemas, skill catalogue, variables and
  conversation. If schemas dominate, the cheaper fix is a tighter per-role `Tools` envelope, which phase
  2.4's charters already give a home. Jev is only worth trying if that is not enough.
- **A second vendor in the hot path of every turn**, adjacent to the parked second-chat-provider idea
  above. It is not a chat provider, so it would not go through `IChatClientProvider`.
- **Scores rescale silently.** The .NET write-up reports that adding a criterion rescales a Score, so a
  fixed threshold quietly changes meaning. In a codebase whose recurring defect is tests that pass for
  the wrong reason, any threshold would need a drift guard.
- **Placement, if built:** a generic `IToolSelector`-style port in Thalos with a deterministic default,
  and the Jev client in the host, per the 2026-09-21 Thalos/Daedalus split.

### Where it must not go

**Review verdicts and gates.** Phase 2.3 requires a rejection to carry file, line and failure scenario
and an approval a non-empty `checked[]`. A bare probability carries no evidence and would reopen phase
2.2's `Succeeded`-without-the-work gap.

Smaller candidates, nice to have and not needed: ranking scout digest items by relevance or urgency,
and a cheap check that skips `retrospect` when the implementer reported no learnings.

Sources: browserbase.com/blog/what-is-jev; dev.to/ohalay/scoring-a2a-agent-skills-with-system-one-jev-in-net-k1n;
gist.github.com/pjburnhill/adf8d28efcad9df037bfdece178ef965.
