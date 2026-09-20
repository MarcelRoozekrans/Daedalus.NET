# ZeroAlloc org adoption review

**Date:** 2026-09-20
**Question:** the ZeroAlloc-Net org ships 27 libraries. Daedalus uses 8 pins covering 5 of them.
Which of the rest should Daedalus adopt, and which would be churn?

**Method:** every claim below is grounded in a count taken from this tree, not from a package
description. Package APIs were verified by reflecting over the shipped assemblies, not by reading
docs — the phase 1.5 `ZeroAlloc.Results` incident was caused by trusting a version number over the
`AssemblyVersion` actually in the package.

## Current state

| Pinned in `Directory.Packages.props` | Version | Actually referenced by a project? |
|---|---|---|
| `ZeroAlloc.Authorization` | 2.1.0 | yes — detached-run policy boundary, phase 1.9 |
| `ZeroAlloc.Mapping` | 1.6.1 | yes |
| `ZeroAlloc.Outbox` / `.EfCore` | 2.5.2 | yes — phases 1.4, 1.5 |
| `ZeroAlloc.Results` | 1.2.2 | **no — pinned, zero project references** |
| `ZeroAlloc.Validation` (+ `.Generator`, `.AspNetCore`) | 1.7.1 / 1.5.6 | **no — pinned, zero usages** |

Two pins are inert. `ZeroAlloc.Results` is pinned only because central *transitive* pinning is on
and it arrives beneath something else — which is precisely how its `AssemblyVersion 0.1.0.0`
defect reached Daedalus in phase 1.5 without any project asking for it.

**Version skew to fix:** `ZeroAlloc.Validation` is 1.7.1 while `.Generator` and `.AspNetCore` are
1.5.6. Align before use.

## Tier 1 — replaces a live third-party dependency (phase 1.7)

Each of these removes a package Daedalus currently ships.

| Adopt | Replaces | Size | Verified |
|---|---|---|---|
| `ZeroAlloc.Validation` | `FluentValidation` + `.DependencyInjectionExtensions` | 8 validators, 1 filter, 2 wire-up sites — 11 files | — |
| `ZeroAlloc.Mediator` 5.1.1 | hand-rolled CQRS — `ICommandHandler`, `IQueryHandler`, 2 factories | 12 commands, 2 queries, 14 handlers, 21 dispatch sites | published, mature |
| `ZeroAlloc.Results` 1.2.2 | `CSharpFunctionalExtensions` `Result<T>` | 151 files, but only **48 combinator sites** | reflected: `Error`, `Value`, `IsSuccess`/`IsFailure`, `Success`, `Failure`, `Bind`, `Combine`, `Match`, `Map`, `Ensure`, `Tap`, `TapError`, `MapError`. `AssemblyVersion 1.2.2.0` — upstream fix confirmed present |
| `ZeroAlloc.ValueObjects` 2.0.7 | `CSharpFunctionalExtensions.ValueObject` | 3 classes | purpose-built: its description names `CSharpFunctionalExtensions.ValueObject.GetEqualityComponents()` as the thing it eliminates |

**Gap: `Entity<TId>` has no ZeroAlloc equivalent.** `ZeroAlloc.ValueObjects` exports
`ValueObjectAttribute`, `EqualityMemberAttribute`, `TypedIdAttribute` and the ULID/UUIDv7/Snowflake
cores — but nothing Entity-shaped. Nine entities derive from CSFE `Entity<TId>`:
`ScheduledRun`, `ScheduledRunExecution`, `AgentMemory`, `AgentMessage`, `BrainstormMessage`,
`ChannelConversation`, `Skill`, `TaskExecution`, `AnalysisIteration`. Contract to reproduce,
taken by reflection:

```csharp
abstract class Entity<TId> : IComparable, IComparable<Entity<TId>>
    where TId : IComparable<TId>
{
    public TId Id { get; protected set; }   // protected setter - EF Core materialisation needs it
    // Equals, GetHashCode, op_Equality, op_Inequality, CompareTo
}
```

Hand-rolled in `Daedalus.Domain`. Characterisation tests are written against the **current CSFE
behaviour first** and must still pass after the swap — transient default-`Id` equality, proxy
types, cross-type inequality, hash stability. Without those, the swap is unfalsifiable.

**Shape change, not a rename:** `ZeroAlloc.ValueObjects` is an attribute plus source generator, so
the 3 value objects go from `sealed class X : ValueObject` with a `GetEqualityComponents()` override
to `[ValueObject] partial class X` with marked members. Strictly better — no boxing, no iterator
allocation, AOT-clean — but the classes must become `partial`.

**New transitive dependencies** under central transitive pinning: `ZeroAlloc.Pipeline` 1.2.2 via
Mediator, and `ZeroAlloc.Serialisation` 2.4.2 via ValueObjects. Neither is pinned today. `NU1109`
is an error and not suppressible, so a missed pin fails the build loudly rather than silently —
the safe failure mode, but it must be done deliberately.

**De-risked:** the `ZeroAlloc.Mediator` defect that killed `ZeroAlloc.Saga` in phase 1.5 — generated
`Publish` ignores DI-registered `INotificationHandler<T>` — **cannot bite here.** Daedalus has zero
notification surface: no `Publish(` and no `INotificationHandler` anywhere in `src`. The bug is real
and still open upstream; it is simply not on this path.

## Tier 2 — strong fit, but new capability rather than replacement

| Candidate | Evidence in this tree | Assessment |
|---|---|---|
| `ZeroAlloc.Analyzers` | — | Analyzer-only, no runtime surface, no AOT risk. Cheapest adoption in the org; worst case it emits warnings we tune. **Take it.** |
| `ZeroAlloc.TestHelpers` | — | Allocation assertions, source-distributed, test-only. **Take it.** |
| `ZeroAlloc.Rest` | 11 files touch `HttpClient`; 6 are hand-rolled API clients — `GitHubApi`, `GitHubPullRequestFactory`, `AzureDevOpsPullRequestFactory`, `AgentApiClient`, `ApiClient`, `ProjectApiClient` | Real hook and AOT-clean, which Milestone 3 wants. But rewriting a working GitHub client that phase 1.9 just proved against live GitHub trades proven code for unproven. **Defer to Milestone 3**, where AOT gives it a reason |
| `ZeroAlloc.Telemetry` | 5 OpenTelemetry packages pinned, **zero** `ActivitySource` or `Meter` in `src` | A genuine gap, not a swap: OTel is wired in `ServiceDefaults` and `Web` but nothing emits custom spans. Source-generated instrumentation would give the scheduling and digest paths real traces. Complements the OTel exporters rather than replacing them. **Worth doing; sizeable enough to deserve its own phase** |

## Tier 3 — belongs to a later milestone

| Candidate | Where |
|---|---|
| `ZeroAlloc.StateMachine` | **Milestone 2.2**, the durable workflow engine. 29 files already carry state-ish enums including `RunStep`. The single best fit in the org for the manufacturing pipeline |
| `ZeroAlloc.EventSourcing` | Milestone 2.2 — candidate for durable, resumable workflow state |
| `ZeroAlloc.ORM` | Milestone 3 — already recorded as the EF Core replacement for Native AOT |
| `ZeroAlloc.Inject` | Milestone 3 — compile-time DI is an AOT prerequisite |
| `ZeroAlloc.Resilience` | Milestone 3 — 2 sites use Microsoft `AddStandardResilience`; swap when AOT forces the question |

## Tier 4 — no hook, or actively wrong

| Candidate | Why not |
|---|---|
| `ZeroAlloc.Saga`, `ZeroAlloc.Scheduling` | **Banned by an architecture test.** Saga never receives its trigger event; the Scheduling EF job store ships no migrations. Dropped in phase 1.5, enforced by a `.csproj` scan |
| `ZeroAlloc.Cache` | Zero caching in the codebase — no `IMemoryCache`, no `IDistributedCache`. Adopting it invents a feature rather than migrating one |
| `ZeroAlloc.Notify`, `ZeroAlloc.Flux` | `INotifyPropertyChanged`: 0 files. No Redux-style store. Blazor plus Radzen wants neither |
| `ZeroAlloc.Collections` | No measured hot path. Adopting pooled collections without a benchmark is speculation |
| `ZeroAlloc.Serialisation` | Arrives transitively via ValueObjects anyway. `System.Text.Json` is used in 14 files and works; no benchmark justifies a swap |
| `ZeroAlloc.AsyncEvents` | No async event surface |
| `ZeroAlloc.Specification` | 1 file touches `IQueryable`. Nothing to generalise |
| `ZeroAlloc.Templates` | A `dotnet new` template, not a dependency |
| `ZeroAlloc.Pipeline` | Generator infrastructure; arrives transitively under Mediator |

## Recommendation (superseded — see Adoption programme below)

Phase 1.7 takes **Tier 1 plus the two free Tier-2 items** — `Analyzers` and `TestHelpers`. That
removes `CSharpFunctionalExtensions` and both `FluentValidation` packages outright, activates two
pins that are currently inert, and adds no runtime risk beyond the migrations themselves.

`Telemetry` and `Rest` are real but are additions rather than migrations; folding them into a phase
that already rewrites 151 files would make the diff unreviewable. `StateMachine` is held for
Milestone 2.2, where it is load-bearing rather than optional.

Counting honestly: 5 libraries adopted today, **11 after phase 1.7**, 13 with Telemetry and Rest,
17 by the end of Milestone 3. The remainder are excluded on evidence, and two of them are banned.


## Adoption programme (decided 2026-09-20)

The tiering above was written to answer "which of these are worth adopting?" The answer given was
"adopt as much as possible", which changes the question to "where does each one go?" Every library
with a real hook now has a named phase. Nothing is dropped for being merely unexciting — the only
exclusions left are ones that cannot be adopted, and each says why.

Two exclusions in the tiering above were too quick and are corrected here:

- **`ZeroAlloc.Cache`** was dismissed as "no caching exists, so adopting it invents a feature." But
  `GitHubApi` calls a **rate-limited** API on every digest. That is a real hook.
- **`ZeroAlloc.StateMachine`** was parked in Milestone 2 as a future fit. `ScheduledRunExecution.Step`
  and `RunStep` already *are* a state machine, with hand-written transitions, today.

| # | Library | Home | Why there |
|---|---|---|---|
| 1 | `Authorization` | adopted | detached-run policy boundary, phase 1.9 |
| 2 | `Mapping` | adopted | — |
| 3 | `Outbox` (+ `.EfCore`) | adopted | phases 1.4, 1.5 |
| 4 | `Results` | **1.7** | replaces CSFE `Result<T>`, 151 files |
| 5 | `Validation` | **1.7** | replaces FluentValidation, 11 files |
| 6 | `Mediator` | **1.7** | replaces the hand-rolled CQRS layer |
| 7 | `ValueObjects` | **1.7** | replaces CSFE `ValueObject`, 3 classes |
| 8 | `Analyzers` | **1.7** | analyzer-only, no runtime surface |
| 9 | `TestHelpers` | **1.7** | test-only allocation assertions |
| 10 | `Pipeline` | **1.7** (transitive) | generator infrastructure beneath Mediator |
| 11 | `Serialisation` | **1.7** (transitive), explicit in **3** | arrives via ValueObjects; the deliberate `System.Text.Json` swap waits for AOT to justify it |
| 12 | `StateMachine` | **2.2** | the durable workflow engine's substrate — and `RunStep` is already a hand-written state machine |
| 13 | `EventSourcing` | **2.2** | durable, resumable workflow state |
| 14 | `AsyncEvents` | **2.2** | event dispatch inside the workflow engine |
| 15 | `Telemetry` | **2.6** | 5 OTel packages pinned, zero `ActivitySource` in `src` — a real gap |
| 16 | `Rest` | **2.7** | replaces 6 hand-rolled `HttpClient` API clients |
| 17 | `Cache` | **2.7** | fronts the rate-limited GitHub API the scout hits every digest — pairs with Rest, same subsystem |
| 18 | `Flux` | **2.8** | Blazor state for the manufacturing console |
| 19 | `ORM` | **3** | EF Core cannot publish AOT |
| 20 | `Inject` | **3** | compile-time DI is an AOT prerequisite |
| 21 | `Resilience` | **3** | the 2 `AddStandardResilience` sites |
| 22 | `Collections` | **3** | pooled collections, once AOT work supplies benchmarks to justify them |
| 23 | `Specification` | **3** | query composition, alongside the ORM migration that gives it a surface |

### Cannot be adopted

| Library | Why |
|---|---|
| `Saga` | **Banned by an architecture test.** Generated `Publish` ignores DI-registered `INotificationHandler<T>`, so a saga never receives its trigger event — [Saga#127](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/127). Un-banning it is upstream work, not a Daedalus change |
| `Scheduling` | **Banned by the same test.** Its EF job store needs a separate context that ships no migrations and cannot be bootstrapped with `EnsureCreated`, which would leave the `Jobs` table silently missing |
| `Templates` | A `dotnet new` template, not a package reference. Adoptable only as structural conformance |
| `Notify` | `INotifyPropertyChanged`: 0 files. Blazor does not use INPC. Revisit only if an MVVM or desktop surface ever appears |

**23 of 27 have a home. 2 are blocked on upstream bugs, 1 is not a package, 1 has no hook.**
