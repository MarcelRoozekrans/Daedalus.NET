# Handover: a JSON-context overload for ZeroAlloc.Outbox's System.Text.Json serializer

**From:** the Daedalus session running phase 3.2, reflection out
**To:** the owner's ZeroAlloc session, for ZeroAlloc.Outbox
**Date:** 2026-10-21

## The problem in one paragraph

Daedalus ships as Native AOT. Phase 3.2 removes every reflective path it can, and one site is left that Daedalus
cannot fix itself: `IOutboxBuilder.WithSystemTextJsonSerializer()` takes no argument and registers
`SystemTextJsonOutboxSerializer`, which serialises through reflection-based System.Text.Json. A trimmed or AOT build
has no metadata for the message types, so that call carries the IL warning below. The only AOT-safe route Outbox
offers today is `[ZeroAllocSerializable]` plus `AddSerializerDispatcher()`, which changes the stored bytes and
needs the ZeroAlloc.Serialisation family. Daedalus wants a middle path: keep System.Text.Json and its bytes, but
let the caller hand over a source-generated `JsonSerializerContext`.

## The call

`src/Daedalus.Agents/Channels/ChannelOutboxServiceCollectionExtensions.cs:103`, in `AddChannelOutbox`:

```csharp
services.AddOutbox(o => { /* polling, batch, attempts, lease */ })
    .WithEfCore<ApplicationDbContext>()
    .AddChannelMessageQueuedOutbox()
    .AddScheduledRunDueOutbox()
    .AddRunScoutStepOutbox()
    .AddRunWriterStepOutbox()
    .AddDeliverDigestOutbox()
    .WithSystemTextJsonSerializer();
```

Package: ZeroAlloc.Outbox 4.4.5, the version in `Directory.Packages.props`.

## The IL warning

Read from the `ZeroAlloc.Outbox.dll` metadata under `~/.nuget/packages/zeroalloc.outbox/4.4.5/lib/net10.0`.
`WithSystemTextJsonSerializer` carries both attributes, with these exact messages:

- `[RequiresUnreferencedCode]`: "WithSystemTextJsonSerializer registers SystemTextJsonOutboxSerializer, which uses
  reflection-based System.Text.Json and may not work after trimming. For trim- and AOT-safe serialisation, call
  services.AddSerializerDispatcher() instead."
- `[RequiresDynamicCode]`: "WithSystemTextJsonSerializer registers SystemTextJsonOutboxSerializer, which uses
  reflection-based System.Text.Json and may need runtime code generation. For AOT-safe serialisation, call
  services.AddSerializerDispatcher() instead."

These surface as IL2026 and IL3050 at the call site. Daedalus allows no suppression for trim or AOT warnings, so the
warning stays visible until the overload exists.

## The message types

Five `[OutboxMessage]` records, all in `Daedalus.Agents`. Their payloads are plain: strings, `Guid`, `Guid?` and
`DateTime`.

| Type | Shape |
|---|---|
| `ChannelMessageQueued` | `(string ChannelId, string ConversationId, string Text, Guid? ExecutionId = null)` |
| `ScheduledRunDue` | `(Guid ScheduleId, DateTime OccurrenceAtUtc)` |
| `RunScoutStep` | `(Guid ExecutionId)` |
| `RunWriterStep` | `(Guid ExecutionId)` |
| `DeliverDigest` | `(Guid ExecutionId)` |

Rows already in the `OutboxMessages` table were written by the reflective serializer with default
`JsonSerializerOptions`. The new path must read them and write the same bytes, so the context must use the same
defaults: no naming policy, no custom converters, the default encoder.

## The overload wanted

Either of these on `IOutboxBuilder`, neither carrying `[RequiresUnreferencedCode]` or `[RequiresDynamicCode]`:

```csharp
IOutboxBuilder WithSystemTextJsonSerializer(JsonSerializerContext context);
IOutboxBuilder WithSystemTextJsonSerializer(JsonSerializerOptions options); // options.TypeInfoResolver must cover the messages
```

Requirements:

- `SystemTextJsonOutboxSerializer` serialises and deserialises through `JsonTypeInfo<T>` from the supplied context or
  resolver, with no `JsonSerializer.Serialize(object, Type)` or other reflective fallback.
- A message type the context does not cover fails loudly, naming the type, instead of falling back to reflection.
  The `JsonSerializerOptions` form should refuse an options instance whose `TypeInfoResolver` is null.
- The type-name to CLR-type mapping the generated `Add*Outbox` methods already do is untouched.
- The existing parameterless overload stays, so current consumers keep working.

Daedalus will declare one `JsonSerializerContext` with `[JsonSerializable]` for the five records above and pass it.

## What Daedalus does until the overload exists

It keeps the reflective call at line 103. That is the one site left, carried as a named blocker in phase 3.2's
rulings. It hides nothing: the IL2026 and IL3050 warnings stay in the baseline count and are not suppressed. Once
the overload is released, Daedalus bumps ZeroAlloc.Outbox, adds the context, and the baseline drops.

## Where to look

- The call: `src/Daedalus.Agents/Channels/ChannelOutboxServiceCollectionExtensions.cs:103`.
- The messages: `Channels/ChannelMessageQueued.cs`, `Scheduling/ScheduledRunDue.cs`, `Scheduling/RunSteps.cs`.
- The phase plan: `docs/superpowers/plans/2026-10-21-phase-3.2-reflection-out-plan.md`.
