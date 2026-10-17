# ZeroAlloc.ORM gap audit: every EF Core use in Daedalus

**Date:** 2026-10-17
**Daedalus:** branch `phase/3.1-aot-foundation` at `b71f17cd`. Paths are relative to the repository root.
**ZeroAlloc.ORM:** tag `v2.3.4`, commit `8aa007c`. Paths marked **ORM** are relative to that checkout.
Daedalus pins 2.3.1; 2.3.2 to 2.3.4 changed only documentation and tests (`CHANGELOG.md:3-36`), so every finding
holds for the pinned version too.
**ZeroAlloc.Outbox:** tag `v4.4.3`, commit `e1f4e0e`, the version Daedalus pins for `ZeroAlloc.Outbox.Orm`.
Paths marked **Outbox** are relative to that checkout.
**Companion document:** `docs/plans/2026-10-17-zeroalloc-linq-handover.md`. This audit extends its query-shape list
with a file:line for every use. Where the two disagree, section 6 says which and why. The handover is unchanged.
**Owner decision:** nothing is filed upstream from this audit. The owner runs the ORM and ZeroAlloc.Linq work in a
separate session, and this document is its input.

## 1. Summary

- **What uses EF Core.** 32 files under `src/` name `ApplicationDbContext`. 22 of them run queries, 6 only wire it up,
  and 4 mention it in comments only (section 2). The model has 21 `DbSet`s, 20 owned by Daedalus plus the
  `ZeroAlloc.Outbox.EfCore` table, 20 `IEntityTypeConfiguration` classes and 26 EF migrations.
- **Three hard gaps, all confirmed** against the 2.3.4 source (section 4):
  1. adopting an EF-created database and its `__EFMigrationsHistory`;
  2. array parameters and array columns;
  3. jsonb parameters.
- **Three further gaps** that block or complicate 3.5 (section 4.4 to 4.6):
  1. unsigned integers, which `xmin` needs, read and bound as `xid` through 4.3's data-type name;
  2. `ZeroAlloc.Outbox.Orm` has no `IOutboxDashboardStore` and a fixed table name;
  3. a migration file named with a 14-digit version is skipped silently.
- **Everything else is supported** or supported-with-work: raw SQL already covers `FOR UPDATE`, `ILIKE`, tuple keyset
  paging, upserts, set-based writes, transactions and multi-result eager loading. The work is the volume, about 117
  SQL statements over 22 files, plus explicit writes where EF's change tracker did them (section 5).
- **Not needed after all:**
  - pgvector, because no Daedalus table has a `vector` column any more, and Rag.NET creates the extension itself;
  - an ORM typed-id converter, because no Daedalus column is a typed id.

  `ZeroAlloc.ValueObjects.EfCore` is only a transitive dependency of `ZeroAlloc.Outbox.EfCore` 4.0.1, and leaves
  with it.
- **A defect found on the way, not an ORM gap:** six aggregates carry a `bytea` `RowVersion` that PostgreSQL never
  generates, so their optimistic concurrency checks nothing (section 3.4). 3.5 must decide whether to port the
  guard properly or drop the columns. It must not port the illusion.

Status legend: **supported** means a `[Query]`/`[Command]` expresses it today with no extra work. **supported-with-work**
means it works today, and the "what the SQL must do" column says how. **gap** means ZeroAlloc.ORM 2.3.4 cannot do it.

## 2. The 32 files

| # | File | Role | Statements 3.5 ports |
|---:|---|---|---:|
| 1 | `src/Daedalus.Agents/Workflow/WorkflowRunRecordStore.cs` | queries | 2 |
| 2 | `src/Daedalus.Agents/Sessions/AgentSessionCrashRecovery.cs` | queries | 1 |
| 3 | `src/Daedalus.Api/Services/TaskExecutionQueryService.cs` | queries | 4 |
| 4 | `src/Daedalus.Api/Services/TaskQueryService.cs` | queries | 6 |
| 5 | `src/Daedalus.Infrastructure/Persistence/ProjectRepository.cs` | queries | 4 |
| 6 | `src/Daedalus.Infrastructure/Services/ProjectQueryService.cs` | queries | 6 |
| 7 | `src/Daedalus.Agents/Scheduling/ScheduleDeliveryActions.cs` | queries, outbox dashboard | 1 |
| 8 | `src/Daedalus.Infrastructure/Persistence/Repositories/BrainstormRepository.cs` | queries | 7 |
| 9 | `src/Daedalus.Agents/Channels/PostgresConversationMap.cs` | queries | 3 |
| 10 | `src/Daedalus.Agents/Charters/PostgresRoleCharterStore.cs` | queries | 7 |
| 11 | `src/Daedalus.Infrastructure/Persistence/TaskRepository.cs` | queries | 6 |
| 12 | `src/Daedalus.Infrastructure/Persistence/Repositories/RepositoryConfigurationRepository.cs` | queries | 6 |
| 13 | `src/Daedalus.Infrastructure/Persistence/FailurePatternDatabase.cs` | queries | 2 |
| 14 | `src/Daedalus.Agents/Scheduling/ScheduledRunStore.cs` | queries, outbox writer | 2 |
| 15 | `src/Daedalus.Agents/Skills/PostgresSkillStore.cs` | queries | 8 |
| 16 | `src/Daedalus.Agents/Sessions/PostgresAgentSessionStore.cs` | queries | 12 |
| 17 | `src/Daedalus.Agents/Memory/PostgresMemoryStore.cs` | queries | 9 |
| 18 | `src/Daedalus.Infrastructure/Services/CostAnalyticsService.cs` | queries | 6 |
| 19 | `src/Daedalus.Agents/Scheduling/ScheduleReconciler.cs` | queries | 3 |
| 20 | `src/Daedalus.Agents/Scheduling/ScheduledRunExecutionStore.cs` | queries, outbox writer | 8 |
| 21 | `src/Daedalus.Agents/Scheduling/ScheduleDiagnostics.cs` | queries, outbox table | 5 |
| 22 | `src/Daedalus.Infrastructure/Persistence/CodeAnalysisRepository.cs` | queries | 9 |
| 23 | `src/Daedalus.Infrastructure/Persistence/ApplicationDbContext.cs` | the model: 21 `DbSet`s, `AddOutboxMessages()` at `:60` | – |
| 24 | `src/Daedalus.Infrastructure/Persistence/ApplicationDbContextFactory.cs` | EF design-time factory | – |
| 25 | `src/Daedalus.ServiceDefaults/AspireExtensions.cs` | `AddDbContextPool` and the pooled factory, `:140`, `:144` | – |
| 26 | `src/Daedalus.Migrations/Program.cs` | `Database.MigrateAsync()`, `:27` | – |
| 27 | `src/Daedalus.Agents/Channels/ChannelOutboxServiceCollectionExtensions.cs` | `WithEfCore<ApplicationDbContext>()`, `:93` | – |
| 28 | `src/Daedalus.Agents/DaedalusSchedulingServiceCollectionExtensions.cs` | scoped store registrations that share the context, `:93-109` | – |
| 29 | `src/Daedalus.Agents/DaedalusAgentsServiceCollectionExtensions.cs` | comments only; registers stores 1, 2, 9, 10, 15 to 17 | – |
| 30 | `src/Daedalus.Agents/Scheduling/ScheduleSweeperService.cs` | comments only | – |
| 31 | `src/Daedalus.Agents/Workflow/WorkflowOutboxDispatchOptions.cs` | comments only | – |
| 32 | `src/Daedalus.Agents/Workflow/WorkflowOutboxDispatchService.cs` | comments only; already on `ZeroAlloc.Outbox.Orm` | – |
| | **Total** | | **117** |

A statement is one distinct SQL text 3.5 has to write, as a `[Query]`/`[Command]` or a ZeroAlloc.Linq query. Eager
loads count two, head and lines, and a change-tracked save counts one per table it writes. The counts are a sizing
estimate, read from the code, not a measurement.

Also in scope:

- the 20 configurations in `src/Daedalus.Infrastructure/Persistence/Configurations/`;
- the 26 migrations in `src/Daedalus.Infrastructure/Migrations/`, 24 timestamped plus `AddMissingTaskColumns`
  (`20260209134036`) and `AddRowVersionConcurrencyTokens` (`20260210073742`);
- `src/Daedalus.Migrations/PreScopingMigrationHistory.cs`, which already bypasses the ORM with raw Npgsql for an
  array parameter (section 4.2).

## 3. Feature inventory, verified against ZeroAlloc.ORM 2.3.4

### 3.1 Column types and mappings

| Feature | Daedalus use, file:line | Status | ORM 2.3.4 evidence | What the hand-written SQL must do |
|---|---|---|---|---|
| Primitives: `uuid`, `integer`, `bigint`, `boolean`, `double precision`, `text`, `varchar(n)`, `bytea` | the whole model, `ApplicationDbContextModelSnapshot.cs`: 36 `uuid`, 34 `integer`, 4 `bigint`, 12 `boolean`, 1 `double precision`, 99 `string` properties | supported | ORM `src/ZeroAlloc.TypeConversions/PrimitiveCatalog.cs:18-41`; ORM `docs/README.md:99` | nothing |
| `timestamp with time zone` as UTC `DateTime` | 41 columns; every store writes `UtcDateTime`, for example `PostgresAgentSessionStore.cs:242` | supported | ORM `PrimitiveCatalog.cs:29`. Inputs carry no `DbType` by default (snapshot `EnumIntMaterializationTests…verified.cs:19-22`), so Npgsql infers `timestamptz` from `Kind=Utc` | keep every `DateTime` parameter `Kind=Utc`, as today |
| `DateTimeOffset` | the EF outbox entity, `ApplicationDbContextModelSnapshot.cs` `OutboxMessageEntity` | supported | ORM `PrimitiveCatalog.cs:34` | nothing |
| `interval` as `TimeSpan` | `TaskExecution.ExecutionDuration`, `src/Daedalus.Domain/Entities/TaskExecution.cs:30` | supported | ORM `PrimitiveCatalog.cs:35`; `tests/ZeroAlloc.ORM.Integration.Tests/Postgres/PostgresIntervalInOutTests.cs` | nothing |
| Enum as `int` | `AgentSessionConfiguration.cs:23-25`, `BrainstormMessageConfiguration.cs:23-33`, `BrainstormSessionConfiguration.cs:19-21`, `CodeAnalysisRequestConfiguration.cs:39-41,93-99`, `TaskConfiguration.cs:41-56` | supported | ORM `ConventionDiscovery.cs:150-163`; `tests/…Integration.Tests/EnumIntRoundTripTests.cs` | nothing |
| Enum as `string` | `ScheduledRunConfiguration.cs:29` (`Origin`), `ScheduledRunExecutionConfiguration.cs:21-22` (`Step`, `FailedAtStep`); queried at `ScheduleReconciler.cs:148`, `ScheduledRunExecutionStore.cs:490` | supported-with-work | `[StoreAsString]` is `AttributeTargets.Enum` only, ORM `src/ZeroAlloc.ORM.Abstractions/StoreAsStringAttribute.cs:7`. Putting it on `ScheduleOrigin` and `RunStep` would make `Daedalus.Domain` reference an ORM package | bind `run.Origin.ToString()` as a `string` and parse in the row record's factory, so the Domain stays persistence-free. A per-parameter or per-column string mapping in the ORM would remove the hand-written conversion |
| Custom converter: `IReadOnlyList<string>` as a comma-joined `varchar(512)` | `ScheduledRunConfiguration.cs:37-46`, `ScheduledRunExecutionConfiguration.cs:35-44` (`Roles`); also written raw at `ScheduledRunExecutionStore.cs:191` | supported-with-work | strings are primitives, ORM `PrimitiveCatalog.cs:28` | `string.Join(',', roles)` on write, `Split(',', RemoveEmptyEntries)` on read, exactly as the converter does. Not an array column |
| Owned types, table-split into columns | `CodeAnalysisRequestConfiguration.cs:26-78`: `RepositoryReference` to 4 columns, `CodeLocation` to 3, `AnalysisOutcome` to 4; queried through the owner at `CodeAnalysisRepository.cs:94` | supported-with-work | composites unpack into columns, ORM `docs/README.md:102`; `tests/…Integration.Tests/CompositeTests.cs`. Daedalus' value objects have private constructors and `Result` factories, so the composite convention cannot construct them | read the flat columns into an Infrastructure row record and rebuild the value objects through their `Create` factories; filter on the column `"RepositoryUrl"` directly |
| jsonb, read and write as a whole document | 7 columns, every one a C# `string`: `AgentMessageConfiguration.cs:24-26`, `AnalysisIterationConfiguration.cs:28-29`, `CodeAnalysisRequestConfiguration.cs:72-74,101-108`, `WorkflowRunRecordConfiguration.cs:39-41` | **gap** for parameters; supported for reads | section 4.3 | reads: `GetString` works today. Writes need `@p::jsonb` until the gap closes |
| `text[]` | 6 columns, every one a private `List<string>` backing field: `AgentMemoryConfiguration.cs:34-38`, `SkillConfiguration.cs:35-39`, `SkillVersionConfiguration.cs:38-42`, `RoleCharterVersionConfiguration.cs:41-45`, `TaskConfiguration.cs:73-79` (two) | **gap** | section 4.2 | none clean. A read can go through `array_to_json("Tags")::text` and a write through `string_to_array`/`json` casts, but that is a workaround, not the port |
| `xmin` as a `uint` concurrency token | `TaskConfiguration.cs:67-70` (mapped `RowVersion`), `ScheduledRunConfiguration.cs:57`, `ScheduledRunExecutionConfiguration.cs:55` (shadow `xmin`) | **gap** for reading a `uint` and for binding one as `xid`, which also needs 4.3; supported-with-work through the stopgap | section 4.4 | read `xmin::text::bigint` as `long`; guard with `AND xmin::text::bigint = @expected`; zero rows affected is the conflict |
| `bytea` `IsRowVersion()` | `AgentSessionConfiguration.cs:33-34`, `BrainstormSessionConfiguration.cs:36-37`, `CodeAnalysisRequestConfiguration.cs:111-112`, `ExecutionSessionConfiguration.cs:27-28`, `ProjectConfiguration.cs:39-40`, `RepositoryConfigurationEntityConfiguration.cs:42-43` | supported (`byte[]`), but see 3.4 | ORM `PrimitiveCatalog.cs:40` | the token is never generated; see 3.4 |
| Store-side defaults | `AnalysisIterationConfiguration.cs:31-35`, `BrainstormSessionConfiguration.cs:29-31`, `ProjectConfiguration.cs:28-36`, `TaskExecutionConfiguration.cs:27-31` | supported | schema-side; nothing in the ORM | `INSERT` lists every column, as EF does, or leaves the column out to take the default |
| Check constraint, unique and filtered indexes | `TaskConfiguration.cs:48`; `ChannelConversationConfiguration.cs:33-35`; `WorkflowRunRecordConfiguration.cs:53-63` | supported | schema-side | they live in the baseline SQL; the stores rely on them for upserts and races |
| Cascade deletes | `AgentMessageConfiguration.cs:33-36`, `BrainstormSessionConfiguration.cs:39-42`, `TaskConfiguration.cs:81-84` | supported | schema-side (`ON DELETE CASCADE`) | `TaskRepository.cs:78-81` already relies on the database cascade |
| The existing schema, quoted PascalCase identifiers | every table, for example `PostgresConversationMap.cs:83` | supported | raw SQL | quote every identifier exactly as EF created it: `"Tasks"`, `"CreatedAt"` |
| Domain entities with private setters and private constructors | all 20 entity types, 13 of them with a private EF-only constructor, for example `src/Daedalus.Domain/Entities/AgentSession.cs:41`, `Skill.cs:62`, `ScheduledRun.cs:97` | supported-with-work | the ORM materialises positional records, one public multi-argument constructor, or `[Materialize(Factory)]` (ORM `docs/README.md:105-109`); EF used private constructors and backing fields | Infrastructure row records per table, plus a rehydration factory per aggregate. This is the largest single cost of 3.5 |

### 3.2 Queries

| Feature | Daedalus use, file:line | Status | ORM 2.3.4 evidence | What the hand-written SQL must do |
|---|---|---|---|---|
| Equality, range and `null` filters with `&&`/`\|\|` | see the catalogue, section 5.1 | supported | raw SQL | `WHERE` clauses |
| Optional filters composed at run time | `PostgresMemoryStore.cs:235-283`, `PostgresSkillStore.cs:163-189`, `WorkflowRunRecordStore.cs:30-32` | supported-with-work | one SQL text per method | `(@kind IS NULL OR "Kind" = @kind)`; a list filter needs an array parameter, so it waits on gap 4.2 |
| `Contains` on a captured list | `TaskQueryService.cs:55`, `PostgresSkillStore.cs:168,213`, `PostgresRoleCharterStore.cs:106`, `PostgresMemoryStore.cs:181,242,254`, `ScheduleDiagnostics.cs:116` | **gap** | an array parameter has no binding, section 4.2 | `= ANY(@ids)` and `<> ALL(@names)`, once arrays bind |
| Membership in an array column | `PostgresSkillStore.cs:183`, `PostgresMemoryStore.cs:268`: `@tag = ANY("Tags")` | supported for the filter, **gap** for reading the row | the scalar `@tag` binds; reading `"Tags"` back is gap 4.2 | `@tag = ANY("Tags")`, or `"Tags" @> @tags` for the AND of several tags once arrays bind |
| `EF.Functions.ILike` | `FailurePatternDatabase.cs:31-32` | supported | raw SQL | `"Error" ILIKE @pattern`, with `'%' + text + '%'` built in C#, unescaped as today |
| `string.Length` | `FailurePatternDatabase.cs:52` | supported | raw SQL | `length("LlmResponse") > 0` |
| Tuple keyset paging | `PostgresMemoryStore.cs:210` | supported | raw SQL | `("CreatedAt", "Id") > (@lastCreatedAt, @lastId) ORDER BY "CreatedAt", "Id" LIMIT @n` |
| Ordering, `Take`, `Skip` | section 5.1 | supported | raw SQL | `ORDER BY … LIMIT @take OFFSET @skip` |
| Projections into anonymous types, records and single columns | `TaskQueryService.cs:56`, `ScheduleDeliveryActions.cs:73`, `CostAnalyticsService.cs:103`, `ScheduleDiagnostics.cs:202`, `PostgresAgentSessionStore.cs:92`, `ScheduledRunExecutionStore.cs:466,492` | supported | positional records and scalar lists, ORM `docs/README.md:94,107` | one row record per shape |
| `Count`, `Any`, `Max`, `Average`, `ToDictionary` | `TaskExecutionQueryService.cs:13,31`; `PostgresAgentSessionStore.cs:84,130,236`; `CostAnalyticsService.cs:272-274`; `PostgresRoleCharterStore.cs:86` | supported-with-work | scalars, ORM `docs/README.md:94` | `count(*)::int`; `EXISTS(…)`; `MAX` as `int?`; `avg(…)::float8` as `double?`; the dictionary is built in C# |
| Correlated `NOT EXISTS`, latest row per group | `ScheduleDiagnostics.cs:114-118` | supported | raw SQL | `WHERE NOT EXISTS (SELECT 1 FROM … later WHERE later."ScheduleId" = e."ScheduleId" AND later."OccurrenceAt" > e."OccurrenceAt")`, or `DISTINCT ON` |
| `GroupBy` with `Sum`, `Count` and `.Date` | `CostAnalyticsService.cs:79-89,143-153` | supported-with-work | raw SQL, `DateOnly` reads, ORM `PrimitiveCatalog.cs:38` | `GROUP BY "ModelId", date_trunc('day', "ExecutedAt", 'UTC')`, or `("ExecutedAt" AT TIME ZONE 'UTC')::date` read as `DateOnly`; `sum(…)::bigint` |
| Joins then `GroupBy` | `CostAnalyticsService.cs:193-212` (two joins), `:234-249` (one join, filtered inner) | supported | raw SQL | `JOIN … GROUP BY`; the second filters `t."ProjectId" = @projectId` |
| `Include` of a collection | `TaskRepository.cs:24`, `TaskQueryService.cs:38`, `ProjectQueryService.cs:79`, `CodeAnalysisRepository.cs:24` | supported | multi-result tuple `(Head, IReadOnlyList<Line>)`, ORM `docs/README.md:95`, `docs/cookbook/multi-result-set.md`, `tests/…/Postgres/PostgresMultiResultSetTests.cs` | two `;`-separated statements into a tuple |
| `Include` on a page | `TaskQueryService.cs:17-23`, `ProjectQueryService.cs:19-24` | supported-with-work | the same tuple, list heads | page the heads in the first statement and select the lines `WHERE "TaskId" IN (SELECT … LIMIT … OFFSET …)` in the second, then stitch in C# |
| Filtered and ordered `Include` | `BrainstormRepository.cs:38` | supported | the same tuple | `ORDER BY "CreatedAt"` in the lines statement |
| `ExecuteUpdate` with computed setters | `AgentSessionCrashRecovery.cs:30-35`, `PostgresAgentSessionStore.cs:177-184,224-229`, `PostgresMemoryStore.cs:180-185`, `PostgresSkillStore.cs:212-216`, `PostgresRoleCharterStore.cs:105-109` | supported | `CommandKind.NonQuery` returns rows affected, ORM `docs/README.md:113` | `UPDATE … SET "TurnCount" = "TurnCount" + 1 …`; callers check rows affected as they do now |
| `ExecuteDelete` | `PostgresConversationMap.cs:103-106`, `PostgresMemoryStore.cs:131-134` | supported | the same | `DELETE … WHERE …` |
| `FromSql … FOR UPDATE` | `PostgresAgentSessionStore.cs:118-122` | supported | raw SQL on a transaction; `Outbox.Orm` already runs `FOR UPDATE SKIP LOCKED` through the ORM, Outbox `src/ZeroAlloc.Outbox.Orm/ClaimPostgresPendingOutboxQuery.cs:30-45` | `SELECT 1 FROM "AgentSessions" WHERE "Id" = @id FOR UPDATE` with the transaction parameter |
| Raw upserts | `PostgresConversationMap.cs:81-87` (`ON CONFLICT DO UPDATE`), `ScheduledRunExecutionStore.cs:120-125,186-193` (`ON CONFLICT DO NOTHING`) | supported | raw SQL | the same SQL as a `[Command]`; `DO NOTHING` returns 0 rows on a redelivery |
| Unique-violation detection | `PostgresSkillStore.cs:85,117-122` (SqlState 23505 on `SkillVersions`) | supported | raw SQL | `INSERT … ON CONFLICT ("Name", "ContentHash") DO NOTHING` replaces the catch-and-retry |

### 3.3 Writes, transactions and the outbox

| Feature | Daedalus use, file:line | Status | ORM 2.3.4 evidence | What the hand-written SQL must do |
|---|---|---|---|---|
| Inserts through `Add` + `SaveChanges` | `WorkflowRunRecordStore.cs:21`, `ProjectRepository.cs:39`, `BrainstormRepository.cs:21`, `PostgresRoleCharterStore.cs:52,58`, `TaskRepository.cs:43`, `RepositoryConfigurationRepository.cs:87`, `PostgresSkillStore.cs:50,78`, `PostgresAgentSessionStore.cs:47,161`, `PostgresMemoryStore.cs:68`, `ScheduleReconciler.cs:176`, `CodeAnalysisRepository.cs:116,224` | supported | `[Command]` and `CommandKind.BulkInsert`, ORM `docs/README.md:113`, `docs/cookbook/bulk-insert.md` | one `INSERT` per table; the message batches at `PostgresAgentSessionStore.cs:134-162` suit `BulkInsert`. Rows with `text[]` or jsonb columns wait on gaps 4.2 and 4.3 |
| Change-tracked updates | `ProjectRepository.cs:54-55`, `TaskRepository.cs:58-59`, `BrainstormRepository.cs:80`, `RepositoryConfigurationRepository.cs:104`, `PostgresRoleCharterStore.cs:62-67`, `PostgresSkillStore.cs:55-83`, `PostgresAgentSessionStore.cs:201-204`, `PostgresMemoryStore.cs:104-112`, `ScheduledRunStore.cs:109-127`, `ScheduleReconciler.cs:159-211`, `ScheduledRunExecutionStore.cs:301-314,382-407`, `CodeAnalysisRepository.cs:165,268,311,353,388,427,470` | supported-with-work | no change tracking, by design: handover non-goals | an explicit `UPDATE` per aggregate. `BrainstormRepository.cs:80` also inserts the messages added since the read, which EF found through the navigation (`BrainstormMessageConfiguration.cs:17-19`); `TaskRepository.cs:58` passes the whole graph to `Update` |
| Deletes through `Remove` or `EntityState.Deleted` | `ProjectRepository.cs:69-79`, `RepositoryConfigurationRepository.cs:123-133`, `TaskRepository.cs:81-82` | supported | `NonQuery` | one `DELETE`, rows affected as "not found"; the task delete keeps its `xmin` guard |
| A transaction around several statements | `ScheduledRunStore.cs:85`, `PostgresAgentSessionStore.cs:115`, `ScheduledRunExecutionStore.cs:146,282,360` | supported | an `IAsyncDbTransaction` parameter, ORM `docs/README.md:68-70,121`. Its integration test runs on SQLite only, `tests/…Integration.Tests/TransactionParameterTests.cs:5-17`; `Outbox.Orm` exercises it on PostgreSQL | open the transaction on the connection and pass it to every method |
| The transaction shared with `EfCoreOutboxStore` | `ScheduledRunStore.cs:116-123`, `ScheduledRunExecutionStore.cs:202,231,238,253-254,404-405` | supported, through `ZeroAlloc.Outbox.Orm` | `OrmOutboxStore.EnqueueAsync` takes the caller's `DbTransaction` and `EnqueueInTransactionAsync` an `IAsyncDbTransaction`, Outbox `src/ZeroAlloc.Outbox.Orm/OrmOutboxStore.cs:82-139` | write the row change explicitly before the enqueue. Today `EfCoreOutboxStore`'s own `SaveChangesAsync` flushes the row change too, `ScheduledRunStore.cs:116-121` |
| `ZeroAlloc.Outbox.EfCore`: the outbox table, worker and writers | `ApplicationDbContext.cs:54,60`, `ChannelOutboxServiceCollectionExtensions.cs:85-103`, migrations `20260821163952_AddChannelMessageOutbox.cs`, `20260927122827_AddOutboxLease.cs` | supported-with-work, plus a **gap** | the store and worker move to `ZeroAlloc.Outbox.Orm`; its schema is equivalent, Outbox `src/ZeroAlloc.Outbox.Orm/OutboxOrmMigrations.cs:40-90` | carry pending rows and leases from `"OutboxMessages"` into the ORM table; the dashboard and the table name are gap 4.5 |
| Reading the outbox table directly | `ScheduleDeliveryActions.cs:66-74`, `ScheduleDiagnostics.cs:195-203` | supported | raw SQL | the same `SELECT`, against whichever table the outbox ends up in |
| `IOutboxDashboardStore.RequeueAsync` | `ScheduleDeliveryActions.cs:49,98` | **gap**, in `ZeroAlloc.Outbox.Orm` | section 4.5 | – |
| `ZeroAlloc.ValueObjects.EfCore` | not referenced by any Daedalus project; it arrives through `ZeroAlloc.Outbox.EfCore` 4.0.1, `src/Daedalus.Api/obj/project.assets.json` | not needed | – | it leaves with `ZeroAlloc.Outbox.EfCore` |
| Typed ids | no column; Thalos ids are unwrapped at the store boundary, for example `PostgresAgentSessionStore.cs:57` (`id.Value`) | supported | value objects bind through `Value`, ORM `docs/README.md:101` | nothing new |

### 3.4 Migrations, history tables and the vector extension

| Feature | Daedalus use, file:line | Status | ORM 2.3.4 evidence | What 3.5 must do |
|---|---|---|---|---|
| The 26 EF migrations and `__EFMigrationsHistory` | `src/Daedalus.Infrastructure/Migrations/`, applied by `src/Daedalus.Migrations/Program.cs:23-28` | **gap** | section 4.1 | – |
| `__zaorm_migrations` | already shared by `OutboxOrmMigrations.Postgres` and `WorkflowOrmMigrations.Postgres`: `src/Daedalus.Migrations/WorkflowEngineMigrator.cs:30-62`, `PreScopingMigrationHistory.cs:36-123` | supported | scoped by source since 2.2, ORM `src/ZeroAlloc.ORM/Migrations/PostgresMigrationDialect.cs:74-89`, `docs/README.md:126` | Daedalus' own schema becomes a third source in the same table |
| The `vector` extension | created by `20260302080051_AddSemanticEmbeddings.cs:13`; no current table uses it: `StructuredLearnings` was dropped by `20260817194349_AddAgentMemories.cs:120-123`, and the snapshot has no `vector` column. Rag.NET's own `EnsureSchemaOnStartup` runs `CREATE EXTENSION IF NOT EXISTS vector` (`Rag.NET.VectorStores.PgVector` 1.0.0), enabled at `DaedalusAgentsServiceCollectionExtensions.cs:1693-1698` | not needed | – | keep `CREATE EXTENSION IF NOT EXISTS vector` in the baseline, so existing and fresh databases match. No ORM support for `vector` is required |

**A defect found on the way: the `bytea` row versions enforce nothing.** EF's Npgsql provider does not generate a
`bytea` row version, and nothing else writes the column. So the six tokens above stay `NULL`, and EF's
`WHERE "RowVersion" IS NULL` always matches. `20261010080350_MakeTaskRowVersionXmin.cs:13-14` says so for `Task`,
which moved to `xmin`. The six, one by one:

- **AgentSession.** `PostgresAgentSessionStore.cs:175-176` knows, and uses in-database increments for the counters.
  Its state change at `:195-209` still relies on the token: the catch at `:206` returns `SessionBusy`, but it can
  only fire when the row was deleted, so a deleted session is reported as busy, not as not found.
- **BrainstormSession:** `BrainstormRepository.cs:83`.
- **RepositoryConfiguration:** `RepositoryConfigurationRepository.cs:107`.
- **Project:** `ProjectRepository.cs:54`.
- **CodeAnalysisRequest:** eight load-mutate-save methods, which load at `CodeAnalysisRepository.cs:144`, `:194`,
  `:253`, `:296`, `:338`, `:373`, `:412` and `:455`.

  These two repositories have no `DbUpdateConcurrencyException` catch. Their `catch (Exception)` blocks log the
  exception and return a generic failure, and a concurrency exception falls into them.
- **ExecutionSession** has a `DbSet` and a configuration but no reader or writer anywhere under `src/`. Its domain
  type is "kept for the historical rows" (`src/Daedalus.Domain/Entities/ExecutionSession.cs:10`), and no foreign key
  points at the table. 3.5 can drop the mapping and port nothing; the table stays in the baseline. Dropping the
  table is a separate decision.

Where a `DbUpdateConcurrencyException` is caught, it fires only when the row was deleted, because then zero rows
match. A straight port must keep that behaviour, treating zero rows as the conflict or not-found result each caller
returns today. The decision for 3.5 is whether to add a real `xmin` guard to these aggregates, as `Task` has, or to
drop the dead columns. This audit only records it.

## 4. The gaps in detail

### 4.1 Adopting the EF schema and `__EFMigrationsHistory`

**What Daedalus does today.**

- `src/Daedalus.Migrations/Program.cs:23-28` runs EF's `MigrateAsync` against `ApplicationDbContext`.
- That applies the 26 C# migrations in `src/Daedalus.Infrastructure/Migrations/` and records them in EF's default
  `__EFMigrationsHistory`. No `MigrationsHistoryTable` override exists anywhere in `src/`.
- The first migration, `AddMissingTaskColumns` (`20260209134036`), creates the base tables. The last is
  `20261014090300_AddNodeSelectionUniqueIndex`.
- Then `Program.cs:48-69` runs the workflow engine's raw-SQL sources through `MigrationRunner` into
  `__zaorm_migrations`.

**What the ORM lacks at 2.3.4.**

- `MigrationRunner` has one public operation, `RunAsync` (ORM `src/ZeroAlloc.ORM/Migrations/MigrationRunner.cs:83,155`).
- It knows only its own history table (ORM `PostgresMigrationDialect.cs:74-89`). There is no baseline, no "mark as
  applied" and no hook that reads another tool's history.
- So on a database EF built, the first ORM migration would run `CREATE TABLE "Tasks"` and fail. On a fresh database
  nothing recreates the EF schema at all.
- Versions are `int` (ORM `src/ZeroAlloc.ORM/Migrations/Migration.cs:32`), so EF's 14-digit migration ids cannot
  become ORM versions (see 4.6).

**A stopgap exists, so 3.5 can start before the ORM fix ships.** `IMigrationSource` and `IMigrationDialect` are public
(ORM `src/ZeroAlloc.ORM/Migrations/IMigrationSource.cs:11`, `IMigrationDialect.cs:21`), and the `MigrationRunner`
constructor takes any implementation (`MigrationRunner.cs:83`). Either of these adopts the EF schema today:

- a Daedalus-side source or dialect wrapper that, before the first run, records the baseline version as applied when
  the EF history holds the last EF migration;
- a baseline script whose body is guarded in PL/pgSQL, `DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM
  "__EFMigrationsHistory" …) THEN … END IF; END $$`, so on an EF-built database it runs, does nothing and is recorded.

Both put adoption logic in Daedalus that belongs in the runner. The gap stands; the stopgap is named so 3.5 is not
blocked on it.

**The smallest feature that closes it.** A baseline on a migration source, applied by the runner:

1. Daedalus ships one baseline script, `001_baseline.sql`: the schema exactly as the 26 EF migrations leave it,
   generated once with `dotnet ef migrations script --idempotent` and checked in.
2. The source declares that version 1 is a baseline, adopted when a SQL predicate is true. Daedalus' predicate would
   be:

   ```sql
   SELECT EXISTS (SELECT 1 FROM "__EFMigrationsHistory"
                  WHERE "MigrationId" = '20261014090300_AddNodeSelectionUniqueIndex')
   ```

3. When the source has no rows in `__zaorm_migrations` and the predicate is true, the runner records version 1
   without running it, under the same advisory lock and transaction as a normal apply. Then it applies 2 and up as
   usual.
4. When the predicate is false and the EF history table exists, the database stopped part-way through EF's chain.
   The runner refuses with a clear error, so a half-migrated database is never "adopted".

`__EFMigrationsHistory` is left in place, read-only, so a rollback to an EF build still sees its history. Dropping
it later is a separate migration.

### 4.2 Array parameters, and array columns

**What Daedalus does today.**

- **Arrays as parameters.** Eight `Contains` filters over a captured list, which Npgsql's EF provider sends as one
  array parameter (`= ANY(@p)`):
  - `TaskQueryService.cs:55` (`uuid[]`);
  - `PostgresSkillStore.cs:168,213`;
  - `PostgresRoleCharterStore.cs:106`;
  - `PostgresMemoryStore.cs:181` (`uuid[]`), `:242`, `:254`;
  - `ScheduleDiagnostics.cs:116` (`uuid[]`).
- **Arrays as columns.** The six `text[]` columns in 3.1 are read and written on every insert and update of
  `AgentMemory`, `Skill`, `SkillVersion`, `RoleCharterVersion` and `Task`.
- **A store that already went around the ORM.** `PreScopingMigrationHistory.cs:51,116-117` uses raw
  `NpgsqlParameter<int[]>` and `NpgsqlParameter<string[]>` with `unnest`, precisely because the ORM cannot bind
  them.

**What the ORM lacks at 2.3.4.**

- `PrimitiveCatalog.GetScalarReaderMethod` recognises exactly one array, `byte[]` (ORM
  `src/ZeroAlloc.TypeConversions/PrimitiveCatalog.cs:40,129-130`).
- `ConventionDiscovery.Resolve` returns `Unknown` for `string[]`, `Guid[]` and `List<T>` (ORM
  `ConventionDiscovery.cs:37-52`). So such a parameter is `ZAO041` (ORM `docs/diagnostics/ZAO041.md`) and such a
  column is `ZAO040`.
- A `[Materialize(Factory)]` parameter must itself be a primitive, so a factory cannot take the array either (ORM
  `docs/cookbook/composites.md:189-191`).
- Only `CommandKind.BulkInsert` accepts a collection, as rows, not as one parameter (ORM
  `docs/cookbook/bulk-insert.md:10-11`).
- The ORM lists it as not supported: ORM `docs/README.md:199` ("table-valued and array parameters") and
  `docs/design/2026-05-30-v1.0-design.md:250` ("Array parameters (Postgres `int[]`) — v2").

**The smallest feature that closes it.** One-dimensional arrays of the existing primitives:

- `T[]`, `IReadOnlyList<T>` and `List<T>` of `string`, `Guid`, `int`, `long` and `DateTime`, as parameters and as
  column types.
- Bind the parameter value as a `T[]`, which Npgsql maps to its array type. Read the column with
  `GetFieldValue<T[]>`, adapting to `List<T>` where the target is a list.
- Gate it on `[Dialect(SqlDialect.PostgreSql)]`, with a diagnostic elsewhere, since only PostgreSQL has array types.
- Value-object elements, such as `IReadOnlyList<SessionId>`, can come later. Daedalus unwraps to `Guid` itself.

### 4.3 jsonb parameters

**What Daedalus does today.**

- Seven jsonb columns, all mapped from a C# `string`:
  - `AgentMessage.ContentJson`, `AgentMessageConfiguration.cs:24-26`, written at `PostgresAgentSessionStore.cs:151`
    and read at `:92-99`;
  - `WorkflowRunRecord.PayloadJson`, `WorkflowRunRecordConfiguration.cs:39-41`, read at
    `CostAnalyticsService.cs:103-110`;
  - `AnalysisIteration.ValidationErrors`, `AnalysisIterationConfiguration.cs:28-29`;
  - four on `CodeAnalysisRequest`, `CodeAnalysisRequestConfiguration.cs:72-74,101-108`.
- EF's provider knows the column type, so it sends the string as a jsonb parameter. No query looks inside a
  document. `PostgresAgentSessionStore.cs:25-27` relies on jsonb normalising key order.

**What the ORM lacks at 2.3.4.**

- An input parameter gets no `DbType` unless `[Param(DbType)]` names one (snapshot
  `tests/ZeroAlloc.ORM.Generator.Tests/Snapshots/EnumIntMaterializationTests.Enum_column_and_parameter_round_trip_via_int_cast#TestApp.Repo.g.verified.cs:19-22`).
  So Npgsql sends a `string` as `text`, and PostgreSQL rejects `text` into a jsonb column without a cast.
- `[Param]` can only say `System.Data.DbType` (ORM `src/ZeroAlloc.ORM.Abstractions/ParamAttribute.cs`), and
  `DbType` has no JSON member. There is no way to set Npgsql's `NpgsqlDbType.Jsonb` or `DataTypeName`.
- Nothing in the ORM's source, tests or docs mentions jsonb.
- Reading is fine: Npgsql returns jsonb through `GetString`.

**The smallest feature that closes it.** A provider data-type name on `[Param]`:

- For example `[Param(DataTypeName = "jsonb")] string payload`, valid under `[Dialect(SqlDialect.PostgreSql)]`.
- The generator emits `((global::Npgsql.NpgsqlParameter)p).DataTypeName = "jsonb"`. The consumer already references
  Npgsql.
- That keeps the SQL free of `::jsonb` casts and serves `json`, `jsonb` and other provider types alike.
- A typed `JsonDocument` parameter is not needed for Daedalus.

Until it lands, `@payload::jsonb` in the SQL works and is a plain cast. Name it in the port as the stopgap it is.

### 4.4 Further gap: unsigned integers, for `xmin`

**What Daedalus does today.**

- Three aggregates use PostgreSQL's `xmin` (type `xid`) as their concurrency token, as a `uint`:
  - `Task.RowVersion`: `src/Daedalus.Domain/Entities/Task.cs:92`, mapped at `TaskConfiguration.cs:67-70`;
  - the shadow `xmin` of `ScheduledRun`, `ScheduledRunConfiguration.cs:57`;
  - the shadow `xmin` of `ScheduledRunExecution`, `ScheduledRunExecutionConfiguration.cs:55`.
- EF adds `AND xmin = @original` to each update and delete, and raises `DbUpdateConcurrencyException` on zero rows.
  The stores turn that into a lost race: `TaskRepository.cs:62-66,87-91`, `ScheduledRunStore.cs:131-135`,
  `ScheduledRunExecutionStore.cs:316-322,409-416`.

**What the ORM lacks at 2.3.4.** The primitive catalogue has no unsigned type. Its comment says "Unsigned / sbyte /
char are deferred to v0.2" (ORM `src/ZeroAlloc.TypeConversions/PrimitiveCatalog.cs:11`), and the switch at `:18-41`
has no `UInt32`. Npgsql reads `xid` only as `uint`, so the column cannot be materialised.

**The smallest feature that closes it.**

- Add `uint`, `ulong` and `ushort` to `PrimitiveCatalog`, read with `GetFieldValue<T>`, with matching scalar casts.
  That closes the **read** side only.
- The **guard** also needs 4.3's provider data-type name, `[Param(DataTypeName = "xid")] uint expected`, so this gap
  depends on 4.3. A plain `uint` parameter cannot bind as `xid`:
  - in Npgsql 10.0.3, which Daedalus pins, every `uint` mapping is registered with `MatchRequirement.DataTypeName`
    for `oid`, `xid`, `cid`, `regtype` and `regconfig`
    (`src/Npgsql/Internal/ResolverFactories/AdoTypeInfoResolverFactory.cs:282-288`), and no other resolver maps
    `uint`, so a parameter with no data-type name has no mapping;
  - `DbType.UInt32` maps to no `NpgsqlDbType` (`src/Npgsql/NpgsqlTypes/NpgsqlDbType.cs:611`), so setting it does not
    help;
  - older Npgsql versions inferred `oid` for a `uint`, and `xid = oid` has no operator, so that never worked either.

  EF Core's provider avoids all of this by setting `NpgsqlDbType.Xid` on the parameter explicitly.
- Until both land, `xmin::text::bigint` read as `long`, with `AND xmin::text::bigint = @expected`, works and is a
  stopgap.

### 4.5 Further gap, in `ZeroAlloc.Outbox.Orm`: no dashboard store, and a fixed table name

**What Daedalus does today.**

- `ScheduleDeliveryActions.cs:49,98` resends a dead-lettered channel message through
  `IOutboxDashboardStore.RequeueAsync`. `EfCoreOutboxStore` registers that interface
  (`DaedalusSchedulingServiceCollectionExtensions.cs:102-104`).
- The channel and RepoDigest pipeline lives in EF's quoted `"OutboxMessages"`.
- The workflow engine already uses `ZeroAlloc.Outbox.Orm`'s unquoted `OutboxMessages`, which PostgreSQL folds to
  `outboxmessages`. It is polled by Daedalus' own `WorkflowOutboxDispatchService`, because a second
  `OutboxWorkerService` in one container would break the first (`src/Daedalus.Agents/Daedalus.Agents.csproj:53-57`,
  `src/Daedalus.Migrations/Program.cs:38-47`).

**What `ZeroAlloc.Outbox.Orm` 4.4.3 lacks.**

- `OrmOutboxStore` implements `IOutboxStore` only. The dashboard store is "deliberately left out rather than
  stubbed" (Outbox `src/ZeroAlloc.Outbox.Orm/OrmOutboxStore.cs:30-34`;
  `OrmOutboxServiceCollectionExtensions.cs:29-32`).
- Every statement names the table `OutboxMessages` literally (Outbox
  `src/ZeroAlloc.Outbox.Orm/OutboxMessageRepository.cs:36,60,76`; `ClaimPostgresPendingOutboxQuery.cs:32,38`).
  So the channel pipeline cannot keep a table of its own: moving it means sharing `outboxmessages` with the workflow
  engine, and deciding which worker polls which types.

**The smallest feature that closes it.**

- `RequeueAsync` on the ORM store, at least; the rest of `IOutboxDashboardStore` only if the dashboard is wanted.
- A configurable table name, or a documented answer for two pipelines in one table.

The second is a design decision for the owner as much as a feature.

### 4.6 Further defect: a 14-digit migration version is skipped silently

`EmbeddedResourceMigrationSource` says its pattern's upper bound is unbounded "so callers can adopt large version
numbers (timestamps, etc.)" (ORM `src/ZeroAlloc.ORM/Migrations/EmbeddedResourceMigrationSource.cs:49-50`). But
it parses the version with `int.TryParse` and **skips the file** when that fails (`:114-117`, `:156-159`).

So `20260209134036_add_missing_task_columns.sql`, the obvious way to carry EF's ids over, would never run, and no
error or diagnostic would say so.

The fix is to throw on a matching name whose version does not fit, or to widen `Migration.Version` to `long`, which
`MigrationRunner` and the history table's `INTEGER` column would have to follow. Either way, 4.1's baseline should
renumber from 1, not reuse EF's ids.

## 5. The ZeroAlloc.Linq catalogue

This extends the handover's "Query shapes to cover". Paths are under `src/` and are abbreviated to the file name
where they are unique.

### 5.1 Every query shape, with its uses

**Filters**

- Equality, range and `null`, with `&&` and `||`:
  - `WorkflowRunRecordStore.cs:30,32`;
  - `AgentSessionCrashRecovery.cs:31`;
  - `TaskExecutionQueryService.cs:13,17,31,35`;
  - `TaskQueryService.cs:39,55`;
  - `ProjectRepository.cs:21,70`;
  - `ProjectQueryService.cs:54,80`;
  - `ScheduleDeliveryActions.cs:68-70`;
  - `BrainstormRepository.cs:39,61`;
  - `PostgresConversationMap.cs:47,104`;
  - `PostgresRoleCharterStore.cs:41,55`;
  - `TaskRepository.cs:25,117`;
  - `RepositoryConfigurationRepository.cs:44,66,124`;
  - `FailurePatternDatabase.cs:31,49-52`;
  - `ScheduledRunStore.cs:88`;
  - `PostgresSkillStore.cs:38,68,132,141,150,189`;
  - `PostgresAgentSessionStore.cs:57,70,84,90,129,178,195,225,236`;
  - `PostgresMemoryStore.cs:83,97,132,248,274,279`;
  - `CostAnalyticsService.cs:102,235,273`;
  - `ScheduleReconciler.cs:148`;
  - `ScheduledRunExecutionStore.cs:153,287,365,378,446,465,490`;
  - `ScheduleDiagnostics.cs:150,159,197-199`;
  - `CodeAnalysisRepository.cs:25,47,72,144,194,253,296,338,373,412,455`.
- Through an owned type's member: `CodeAnalysisRepository.cs:94`, `r.Repository.Url`.
- Enum comparisons, stored as `int`: `AgentSessionCrashRecovery.cs:31`, `PostgresAgentSessionStore.cs:225`,
  `CodeAnalysisRepository.cs:47,72`, `ScheduleDeliveryActions.cs:69`, `ScheduleDiagnostics.cs:198`.
- Enum comparisons, stored as `string`: `ScheduleReconciler.cs:148`, `ScheduledRunExecutionStore.cs:490`.
- `Contains` on a captured list, as `= ANY(@array)`:
  - `TaskQueryService.cs:55`;
  - `PostgresSkillStore.cs:168`;
  - `PostgresMemoryStore.cs:181,242,254`;
  - `ScheduleDiagnostics.cs:116`.
- Its negation, as `<> ALL(@array)`: `PostgresSkillStore.cs:213`, `PostgresRoleCharterStore.cs:106`.
- Membership in an array column, `@tag = ANY("Tags")`: `PostgresSkillStore.cs:183`, `PostgresMemoryStore.cs:268`.
  Both use `EF.Property<List<string>>` on a private backing field.
- `EF.Functions.ILike`: `FailurePatternDatabase.cs:32`.
- `string.Length`: `FailurePatternDatabase.cs:52`.
- A constant-false predicate: `PostgresSkillStore.cs:179`, `PostgresMemoryStore.cs:265`.
- A nullable `.Value` inside the predicate: `TaskQueryService.cs:55`.
- Conditional composition, a `Where` added only when an option is set: `WorkflowRunRecordStore.cs:31-32`,
  `PostgresSkillStore.cs:165-190`, `PostgresMemoryStore.cs:239-280`.
- A correlated sub-query, `!Any(...)` for the latest row per group: `ScheduleDiagnostics.cs:116-117`.

**Ordering and paging**

- `OrderBy`/`ThenBy`/`Descending`:
  - `WorkflowRunRecordStore.cs:35-36`;
  - `TaskExecutionQueryService.cs:18,36`;
  - `TaskQueryService.cs:20`;
  - `ProjectQueryService.cs:20`;
  - `ScheduleDeliveryActions.cs:71`;
  - `BrainstormRepository.cs:62`;
  - `TaskRepository.cs:118`;
  - `RepositoryConfigurationRepository.cs:23`;
  - `FailurePatternDatabase.cs:33,53`;
  - `PostgresAgentSessionStore.cs:71,91`;
  - `PostgresMemoryStore.cs:157,214`;
  - `ScheduledRunExecutionStore.cs:491`;
  - `ScheduleDiagnostics.cs:100,160,200`;
  - `CodeAnalysisRepository.cs:48-49,73,95`.
- `Take`/`Skip`:
  - `TaskExecutionQueryService.cs:19-20,37-38`;
  - `TaskQueryService.cs:21-22`;
  - `ProjectQueryService.cs:21-22`;
  - `ScheduleDeliveryActions.cs:72`;
  - `FailurePatternDatabase.cs:34`;
  - `PostgresAgentSessionStore.cs:72`;
  - `PostgresMemoryStore.cs:158,215`;
  - `ScheduleDiagnostics.cs:161,201`;
  - `CodeAnalysisRepository.cs:50`.
- Keyset paging on a tuple, `EF.Functions.GreaterThan(ValueTuple.Create(...), ...)`: `PostgresMemoryStore.cs:210`.
- Deliberately not ordered in SQL, because the contract wants ordinal order: `PostgresSkillStore.cs:192-197`.

**Projections and terminal operators**

- `Select` into anonymous types: `TaskQueryService.cs:56`, `ScheduleDeliveryActions.cs:73`,
  `CostAnalyticsService.cs:81-88,103,145-152,203-211,240-248`.
- `Select` into a record: `ScheduleDiagnostics.cs:202`.
- `Select` of one column: `PostgresAgentSessionStore.cs:92`, `ScheduledRunExecutionStore.cs:466`, and
  `ScheduledRunExecutionStore.cs:492` with a cast to `DateTime?`.
- `CountAsync`: `TaskExecutionQueryService.cs:13,31` (with a predicate), `TaskQueryService.cs:15`,
  `ProjectQueryService.cs:17`, `PostgresMemoryStore.cs:149`.
- `AnyAsync`: `PostgresRoleCharterStore.cs:41`, `PostgresSkillStore.cs:68`, `PostgresAgentSessionStore.cs:84,236`.
- `FirstOrDefaultAsync`: 32 sites, for example `ProjectRepository.cs:21`, `PostgresSkillStore.cs:38`.
- `SingleOrDefaultAsync`: `ScheduledRunExecutionStore.cs:153,287,365,378,447,467`.
- `MaxAsync` over `int?`: `PostgresAgentSessionStore.cs:130`.
- `AverageAsync` over `double?`: `CostAnalyticsService.cs:274`.
- `ToDictionaryAsync`: `PostgresRoleCharterStore.cs:86`.
- A whole table: `PostgresRoleCharterStore.cs:85-86`.

**Aggregation**, all in `CostAnalyticsService.cs`:

- `GroupBy` on an anonymous key with `.Date`, then `Sum` of `(long)` casts and `Count`: `:80-89`, `:144-153`.
- `Join` ⋈ `Join`, Executions ⋈ Tasks ⋈ Projects, then `GroupBy` and `Sum`/`Count`: `:194-212`.
- One `Join` onto a filtered inner query, Executions ⋈ Tasks where `ProjectId` matches, then `GroupBy`: `:235-249`.

**Eager loading**

- `Include` of a collection: `TaskRepository.cs:24`, `TaskQueryService.cs:38`, `ProjectQueryService.cs:79`,
  `CodeAnalysisRepository.cs:24`.
- `Include` on a paged query: `TaskQueryService.cs:19` (before `OrderBy`/`Skip`/`Take`), `ProjectQueryService.cs:23`
  (after them). EF pages the heads in both.
- Filtered and ordered: `BrainstormRepository.cs:38`, `Include(s => s.Messages.OrderBy(m => m.CreatedAt))`.

**Set-based writes**

- `ExecuteUpdate` with constant setters: `AgentSessionCrashRecovery.cs:32-34`, `PostgresAgentSessionStore.cs:226-228`,
  `PostgresSkillStore.cs:214-215`, `PostgresRoleCharterStore.cs:107-108`.
- `ExecuteUpdate` with computed setters, `x => x.Count + 1`: `PostgresAgentSessionStore.cs:179-183`,
  `PostgresMemoryStore.cs:182-184`.
- `ExecuteDelete`: `PostgresConversationMap.cs:103-105`, `PostgresMemoryStore.cs:131-133`.
- Each caller reads rows affected: `PostgresAgentSessionStore.cs:186,231`, `PostgresMemoryStore.cs:136`.

**Raw SQL that stays raw.** A `.ForUpdate()` hook or `[Command]` would serve:

- `FromSql … FOR UPDATE`: `PostgresAgentSessionStore.cs:118-122`.
- `INSERT … ON CONFLICT DO UPDATE`: `PostgresConversationMap.cs:81-87`.
- `INSERT … ON CONFLICT DO NOTHING`: `ScheduledRunExecutionStore.cs:120-125,186-193`.

**Concurrency guards**

- `xmin`, a `uint`:
  - `TaskRepository.cs:58-66,81-91`;
  - `ScheduledRunStore.cs:114-135`;
  - `ScheduleReconciler.cs:211`;
  - `ScheduledRunExecutionStore.cs:301-322,382-417`.
- State-in-WHERE, `UPDATE … WHERE "State" = @from`: `PostgresAgentSessionStore.cs:224-229`.
- Zero rows treated as a deleted row: `PostgresMemoryStore.cs:114`, plus the five `bytea` sites in 3.4.
- Unique-violation as lost race: `PostgresSkillStore.cs:85-102`.

**Transactions**

- Opened by the store: `ScheduledRunStore.cs:85`, `PostgresAgentSessionStore.cs:115`,
  `ScheduledRunExecutionStore.cs:146,282,360`.
- Shared with the outbox writer: `ScheduledRunStore.cs:122`,
  `ScheduledRunExecutionStore.cs:202,231,238,253-254,404-405`.
- Rolled back on a lost race: `ScheduledRunExecutionStore.cs:198,321,415`.

### 5.2 What 3.5 ports

- **22 files, about 117 statements**, per the table in section 2. The largest are `PostgresAgentSessionStore.cs` (12),
  `PostgresMemoryStore.cs` and `CodeAnalysisRepository.cs` (9 each), and `PostgresSkillStore.cs` and
  `ScheduledRunExecutionStore.cs` (8 each).
- **Writes become explicit** at about 30 change-tracked call sites (3.3), and the 20 entity types gain rehydration
  factories in place of EF's private constructors (3.1).
- **Stores that move to `ZeroAlloc.Outbox.Orm`.**
  - Writers sharing a transaction: `ScheduledRunStore` and `ScheduledRunExecutionStore`.
  - Readers of the outbox table or the dashboard: `ScheduleDiagnostics` and `ScheduleDeliveryActions`; the second
    is blocked by gap 4.5.
  - Their wiring: `ChannelOutboxServiceCollectionExtensions` and `DaedalusSchedulingServiceCollectionExtensions`.
  - The table itself, from `ApplicationDbContext.cs:60`.

  `WorkflowOutboxDispatchService` is already on it.
- **Deleted outright:**
  - `ApplicationDbContext.cs`, `ApplicationDbContextFactory.cs`, the 20 configurations, and the 26 migrations with
    their designers and snapshot;
  - the EF registration in `AspireExtensions.cs:131-147`, which becomes an `NpgsqlDataSource`.

  `Daedalus.Migrations/Program.cs:22-28` becomes one more `MigrationRunner` source with the 4.1 baseline.
- **Tests:**
  - the integration fixture's `EnsureCreatedAsync` schema (`DaedalusAgentsOptions.cs:278`) moves to the baseline
    script;
  - the test projects that reference EF (`Daedalus.Tests.Integration.csproj:30,32`,
    `Daedalus.Tests.Unit.Infrastructure.csproj:26`) move off `DbContext`.

**Size.** 3.5 is the largest phase in the milestone, as the milestone design already expects:

- about 117 statements;
- about 30 explicit writes;
- about 20 rehydration factories;
- one baseline migration;
- an outbox move with data carried over;
- fixture rework under a 745-test parity bar.

ZeroAlloc.Linq keeps most of the 117 close to today's LINQ. The roughly five raw statements and the explicit writes
are the same work either way.

**What blocks which part of 3.5:**

- **4.1, schema adoption, blocks nothing.** The stopgap in 4.1 lets 3.5 start before the ORM fix ships.
- **4.2, arrays, blocks every store that reads or writes a `text[]` column or filters with `Contains`:**
  - `PostgresMemoryStore`, `PostgresSkillStore` and `PostgresRoleCharterStore`;
  - the `Task` reads and writes in `TaskRepository`, `TaskQueryService` and `ProjectQueryService`;
  - `ScheduleDiagnostics`.

  Its only workaround is the `array_to_json` and `string_to_array` casting in 3.1, which this audit does not count
  as a port. The other stores can move without it.
- **4.3, jsonb, blocks nothing.** The `@p::jsonb` cast is a stopgap for `PostgresAgentSessionStore`,
  `WorkflowRunRecordStore` and `CodeAnalysisRepository`, to be removed once the gap closes.
- **4.4, unsigned integers, blocks nothing.** The `xmin::text::bigint` stopgap covers `TaskRepository`,
  `ScheduledRunStore`, `ScheduleReconciler` and `ScheduledRunExecutionStore`.
- **4.5, `Outbox.Orm`, blocks the outbox move and `ScheduleDeliveryActions`' resend** until the dashboard and
  table-name questions are answered.
- **4.6 blocks nothing,** as long as the baseline numbers from 1.

## 6. Where the handover is wrong or incomplete

The handover is left unchanged. This audit is the corrected record.

1. **jsonb does not hold owned objects.** The handover says the jsonb columns hold "owned objects and payload
   documents".
   - The owned types (`RepositoryReference`, `CodeLocation`, `AnalysisOutcome`) are table-split into ordinary
     columns (`CodeAnalysisRequestConfiguration.cs:26-78`).
   - All seven jsonb columns are C# `string`s.
   - The jsonb need is therefore exactly "bind a string as jsonb" (4.3), not object mapping.
2. **The second cost query is a one-way join.** The handover says both `CostAnalyticsService` ranges, `:194-215` and
   `:239-252`, are "a two-way join Executions ⋈ Tasks ⋈ Projects".
   - Only the first is.
   - The second, `GetCostsByProjectIdAsync`, is one join onto a filtered `Tasks` (`:235-249`).
   - The last lines of both ranges (`:214-215`, `:251-252`) are already in-memory LINQ, not SQL.
3. **"Array parameters, which the README lists as not supported."** The 2.3.4 root `README.md` no longer says so.
   The reference `docs/README.md:199` and the design `docs/design/2026-05-30-v1.0-design.md:250` do. The claim
   stands; its source moved.
4. **"Typed ids (ZeroAlloc.ValueObjects)" as a column type to handle.** No Daedalus column is a typed id. Ids are
   `Guid` columns, unwrapped at the store boundary. The milestone design's "typed-id persistence gets an ORM-side
   converter" has nothing to convert in Daedalus' own tables.
5. **Shapes missing from the handover's list:**
   - the correlated `NOT EXISTS` (`ScheduleDiagnostics.cs:116-117`);
   - `MaxAsync` and `AverageAsync` (`PostgresAgentSessionStore.cs:130`, `CostAnalyticsService.cs:274`);
   - `string.Length` (`FailurePatternDatabase.cs:52`);
   - negated `Contains` (`PostgresSkillStore.cs:213`, `PostgresRoleCharterStore.cs:106`);
   - array-column membership through `EF.Property` on a private field (`PostgresSkillStore.cs:183`,
     `PostgresMemoryStore.cs:268`);
   - owned-member predicates (`CodeAnalysisRepository.cs:94`);
   - conditional composition (5.1);
   - `Include` on a paged query (`TaskQueryService.cs:19`, `ProjectQueryService.cs:23`);
   - the two raw upserts (5.1).
6. **Missed or not mentioned:**
   - the handover does name `IsRowVersion()` byte tokens and the `xmin` guard (handover `:67-68`). What it misses is
     that the `bytea` tokens enforce nothing (3.4), and that the ORM can neither read a `uint` nor bind one as `xid`
     (4.4);
   - the `ZeroAlloc.Outbox.Orm` dashboard and table-name gap (4.5);
   - the silently skipped 14-digit migration version (4.6).

## 7. Method

Read for this audit:

- every one of the 32 files, the 20 configurations and `ApplicationDbContextModelSnapshot.cs`;
- the migrations that matter for the gaps, read in full: `AddMissingTaskColumns`, `AddRowVersionConcurrencyTokens`,
  `AddSemanticEmbeddings`, `AddAgentMemories`, `MakeTaskRowVersionXmin`, `AddChannelMessageOutbox` and
  `AddOutboxLease`.

The ORM and Outbox claims were checked in their source at the stated tags. Nothing was built, tested or run: a timing
measurement held the machine. The statement counts in section 2 are therefore an estimate from reading the code.
