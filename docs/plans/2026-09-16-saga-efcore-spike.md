# Spike: `ZeroAlloc.Saga` + `ZeroAlloc.Saga.EfCore` compatibility

**Date:** 2026-09-17
**Plan:** `docs/plans/2026-09-16-thalos-subagents-plan-b.md`, Task 1
**Verdict:** **FAIL — but not for the predicted reason.** The phase changes shape.
**Spike project:** throwaway, deleted. Built outside the repository; see *Deviations*.

## Summary

The pairing this task was written to de-risk — **`ZeroAlloc.Saga` 2.0.0 against
`ZeroAlloc.Saga.EfCore` 1.3.0, on EF Core 10 — works.** That risk did not materialise.

A different blocker did, and it is worse, because it is not a version skew that pinning can
resolve: **`ZeroAlloc.Saga` cannot be triggered through `ZeroAlloc.Mediator`'s generated
`IMediator.Publish`.** A saga never receives its own trigger event, so no saga can start. This
reproduces on every Saga/Mediator generation tested and in every project layout tried.

## Versions exercised

| Package | Version | Note |
|---|---|---|
| `ZeroAlloc.Saga` | 2.0.0, 1.6.0 | 1.6.0 tested as the plan's fallback |
| `ZeroAlloc.Saga.EfCore` | 1.3.0 | declares `Saga >= 1.3.0` and Relational 9.0.4; targets net8.0 **and net10.0** |
| `ZeroAlloc.Mediator` | 5.0.0, 3.0.0 | Saga 2.0.0 declares 5.0.0; Saga 1.3.0/1.6.0 declare 3.0.0 |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.0 | Daedalus's pin |
| `Microsoft.EntityFrameworkCore.Relational` | 10.0.2 | Daedalus's pin — the 9.0.4 vs 10.0.2 tension under test |
| PostgreSQL | 16 | real server in Docker, port 5433 |

## What works — the predicted risk did not materialise

1. **The Saga/Saga.EfCore version skew is benign.** `Saga.EfCore` 1.3.0 was compiled against
   `Saga` 1.3.0, but `ZeroAlloc.Saga.ISagaBuilder` kept its identity in 2.0.0, so
   `.WithEfCoreStore<TContext>()` binds and compiles against Saga 2.0.0.
2. **The EF 9.0.4 vs EF 10.0.2 tension is benign.** `modelBuilder.AddSagas()` from the
   9.0.4-declaring package applied cleanly on EF Core 10.0.2, and `EnsureCreatedAsync()` created
   the `SagaInstance` schema against real PostgreSQL 16. Observed `schema created`, then a
   successful read back through `Set<SagaInstanceEntity>()` — the 1.3.0 store was created and
   queried under Saga 2.0.0 on EF 10.
3. The Saga generator emits its FSM, per-event notification handlers, compensation dispatcher and
   builder extension without error under 2.0.0.

**Any plan step that assumed the store or the EF pin was the risk can proceed.** That is not where
this fails.

## What fails

`WithSmokeSaga()` — generator-emitted — registers the saga's event handlers **by interface**:

```csharp
builder.Services.AddTransient<INotificationHandler<global::SmokeStarted>, SmokeSaga_SmokeStarted_Handler>();
```

`ZeroAlloc.Mediator`'s generator emits an `IMediator.Publish` that dispatches to a **closed list of
concrete handler types discovered in its own compilation**, resolved as concrete services:

```csharp
// generated MediatorService.Publish(SmokeStarted ...)
GetRequiredService<StartNudgeHandler>()   // a concrete type, never INotificationHandler<T>
```

It never enumerates `INotificationHandler<T>` from DI. **Saga registers by interface; Mediator
dispatches by compile-time concrete type. The two never meet.**

Two consequences follow, and together they close every door:

- **In the saga's own assembly**, no `Publish` overload is emitted for the trigger event at all.
  The only `INotificationHandler` for that event is emitted by the *Saga* generator, and source
  generators cannot observe each other's output within one compilation. Result: does not compile.
- **In another assembly**, `Publish` can be emitted — but only by declaring a local handler for the
  event, and it then dispatches *only* to that local handler. The saga is never called.

### Evidence

Two-assembly layout, saga in `SagaLib`, publish from `Host`, against real PostgreSQL:

```text
schema created
publishing SmokeStarted d2304b95-bd2f-44c5-9185-30bb7423220b
  [host nudge] saw SmokeStarted d2304b95-bd2f-44c5-9185-30bb7423220b
SagaInstance rows: 0  states: []
```

The host's own handler ran. **The saga was never invoked and no row was ever written.**

### What was ruled out

| Hypothesis | Result |
|---|---|
| Missing `ZeroAlloc.Mediator.Generator` package reference | Ruled out — added; `IMediator` then exists |
| Handlers not registered, so nothing emitted | Confirmed as the *emission* rule, but not a fix |
| `internal` generated handlers invisible across assemblies | Ruled out — `InternalsVisibleTo` changes nothing |
| A Saga 2.0.0 generator regression | Ruled out — **Saga 1.6.0 with Mediator 3.0.0 fails identically** |
| The Saga / Saga.EfCore version skew | Ruled out — that pairing compiles and reaches the database |

## Impact on the plan's stated options

Task 1 step 5 lists three fallbacks. This finding changes all three:

1. **"Pin `ZeroAlloc.Saga` to 1.3.0 and forgo the 2.0.0 generator fix"** — **tested, does not
   work.** 1.6.0 with its declared Mediator 3.0.0 fails the same way. Pinning cannot fix a
   mismatch present in both generations.
2. **"Raise a fix upstream and wait for a `Saga.EfCore` 2.x"** — aimed at the wrong package.
   `Saga.EfCore` is not at fault. Note also that `Saga.EfCore` has shipped nothing since 1.3.0
   while `Saga` and `Saga.Outbox` both reached 2.0.0, so no 2.x is pending.
3. **"Fall back to the InMemory store"** — does not help. The store is not the blocker; the saga is
   never reached regardless of which store is configured.

## Recommendation

Decide between:

- **Fix upstream.** Both `ZeroAlloc.Saga` and `ZeroAlloc.Mediator` are in the ZeroAlloc-Net org.
  The narrow fix is to make the generated `Publish` also dispatch to `INotificationHandler<T>`
  implementations resolved from DI, rather than only to compile-time concrete types. That unblocks
  the phase as designed, and fixes a defect that currently makes `ZeroAlloc.Saga` unusable for any
  consumer.
- **Drop `ZeroAlloc.Saga` from phase 1.5 plan B** and orchestrate the scheduled-run workflow
  directly, keeping `ZeroAlloc.Outbox` for transactional dispatch. Tasks 10–12 are the ones written
  around the saga and would be rewritten.

Either way the phase changes shape — which is exactly what this spike existed to discover, at the
cost of an hour rather than a rewrite at Task 9.

Tasks 2–9 do not depend on the saga and are unaffected.

## Deviations from the task as written

- **Step 1** — `Thalos.NET 0.5.0` was confirmed live on nuget.org, satisfying the gate. When the
  spike began the package was pushed but not yet indexed; the spike does not consume `Thalos.NET`,
  so non-dependent work proceeded and the gate was satisfied before this verdict was recorded.
- **Step 2** — the spike was **not** created under `spikes/` in this repository. Daedalus enables
  `ManagePackageVersionsCentrally`, so `dotnet add package --version` would have written versions
  into the root `Directory.Packages.props` — a shared file — for a project the task says is
  throwaway and must not reach CI. It was built in a scratch directory and deleted. Nothing but
  this note was added to the repository.
- **Step 3** — the README is right about `WithEfCoreStore<TContext>()`. Corrections found against
  the shipped 1.3.0 API: the entity is `SagaInstanceEntity`, not `SagaInstance`; model
  configuration is `modelBuilder.AddSagas()`; handler methods are `Handle`, not `HandleAsync`; and
  every command type needs a handler in the same compilation or the Mediator generator fails with
  `ZAM001`.
- **Step 4** — ran against PostgreSQL 16 in Docker as specified. Schema creation and store reads
  succeeded; the saga itself was never reached, for the reason above.
