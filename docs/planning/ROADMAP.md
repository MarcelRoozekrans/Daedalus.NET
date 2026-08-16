# Project Roadmap

> **Direction (2026-08-14):** Daedalus pivots away from the Ralph Loop orchestrator
> model toward a Hermes-like, .NET-oriented autonomous agent framework, integrating
> Rag.NET (RAG pipeline) and AI.Sentinel (LLM security middleware). Pre-pivot work
> (Ralph Loop, brainstorm sessions, costs dashboard, MCP knowledge base) is treated
> as baseline; reusable parts will be identified during design.
>
> **Refinement (2026-08-16):** The agent framework is a standalone library,
> **Thalos.NET** (separate repo + nuget.org, ZeroAlloc-native, built on Microsoft Agent
> Framework 1.17). Daedalus is its first consumer. Ralph is retired via the strangler
> pattern in phase 1.6. See `docs/plans/2026-08-16-thalos-agent-core-design.md`.

## Milestone 1: Hermes-Style Agent Framework [status: active]
**Goal:** Replace the Ralph Loop setup with a Hermes-like .NET agent framework (Thalos.NET) that integrates Rag.NET and AI.Sentinel.
**Started:** 2026-08-14
**Definition of Done:**
- [ ] All planned phases complete
- [ ] All tests passing
- [ ] Regression test PASS
- [ ] Documentation complete

### Phases

GitHub milestone: [Milestone 1](https://github.com/MarcelRoozekrans/daedalus/milestone/1)

| # | Phase | Status | Depends on | GH issue | Design / Plan |
|---|---|---|---|---|---|
| 1.1 | Thalos.NET core + AI.Sentinel + Daedalus HTTP/Blazor channel | complete (2026-08-16; nuget.org publish of Thalos.NET 0.1.0 pending, see #227) | — | #227 | design: `docs/plans/2026-08-16-thalos-agent-core-design.md` · plan A (Thalos.NET repo): `docs/plans/2026-08-16-thalos-net-plan-a.md` · plan B (Daedalus): `docs/plans/2026-08-16-thalos-net-plan-b.md` · regression: `docs/regression-report-2026-08-16.md` |
| 1.2 | Memory: `Thalos.NET.Memory` port + Rag.NET adapter (pgvector), replaces hand-rolled learnings slice | pending | 1.1 | #228 | — |
| 1.3 | Skills: reusable procedures the agent loads/refines (Rag-backed) | pending | 1.2 | #229 | — |
| 1.4 | Channels: Telegram (+ CLI) via `IChannelAdapter` + `ZeroAlloc.Outbox` | pending | 1.1 | #230 | — |
| 1.5 | Subagents & scheduling: `ZeroAlloc.Saga` orchestration, `ZeroAlloc.Scheduling` autonomous runs | pending | 1.1 | #231 | — |
| 1.6 | Ralph retirement + Daedalus ZeroAlloc migration (CSFE→Results, FluentValidation→Validation, CQRS→Mediator) | pending | 1.2–1.5 | #232 | — |
| 1.7 | Thalos.NET 1.0 release, docs, architecture-diagrams rewrite | pending | all | #233 | — |
