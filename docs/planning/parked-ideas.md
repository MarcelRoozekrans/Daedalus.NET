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
