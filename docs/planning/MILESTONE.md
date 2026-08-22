# Milestone 1: Hermes-Style Agent Framework

**Status:** active
**Started:** 2026-08-14

## Goal

Transform Daedalus from a Ralph Loop orchestrator into a Hermes-like, .NET-oriented
autonomous agent framework. The Ralph Loop setup is retired. The new framework
provides a general agentic core (agent loop, tool use, sessions) and integrates the
existing in-house libraries: **Rag.NET** (retrieval-augmented memory and knowledge)
and **AI.Sentinel** (security monitoring, detection, and approval workflows at the
model boundary).

## Definition of Done

- [ ] All planned phases complete
- [ ] All tests passing (`dotnet test`, all suites)
- [ ] Regression test PASS (web UI verified via Playwright with screenshots)
- [ ] Documentation complete (design + plan docs in `docs/plans/` for each phase)

## Phases

The framework is built as a standalone library, **Thalos.NET** (separate repo
`C:\Projects\Prive\Thalos.NET`, published to nuget.org, ZeroAlloc-native, on Microsoft
Agent Framework 1.17). Daedalus consumes it. Design: `docs/plans/2026-08-16-thalos-agent-core-design.md`.

| # | Phase | Status |
|---|---|---|
| 1.1 | Thalos.NET core + AI.Sentinel + Daedalus HTTP/Blazor channel | complete (2026-08-17; Thalos.NET 0.1.1 on nuget.org, #227) |
| 1.2 | Memory: `Thalos.NET.Memory` port + Rag.NET adapter | complete (2026-08-17; Thalos.NET 0.2.0 on nuget.org, #228) |
| 1.3 | Skills: agent-scoped procedure documents | complete (2026-08-19; Thalos.NET 0.3.0 on nuget.org, #229) |
| 1.4 | Channels: Telegram (+ CLI) | active (started 2026-08-20) |
| 1.5 | Subagents & scheduling | pending |
| 1.6 | Ralph retirement + Daedalus ZeroAlloc migration | pending |
| 1.7 | Thalos.NET 1.0 release + docs | pending |

## Audit History

| Date | Verdict | Gaps |
|---|---|---|
