---
name: daedalus-migrations
description: How to add, verify and apply a schema migration in the Daedalus repo.
tags: [dotnet, zeroalloc-orm, database, daedalus]
---

# Adding a schema migration

Daedalus' schema is a numbered list of plain SQL files, embedded in `src/Daedalus.Infrastructure` and applied by
ZeroAlloc.ORM's `MigrationRunner`. There is no EF Core: no model, no snapshot, no `dotnet ef`.

## 1. Write the SQL file

- Folder: `src/Daedalus.Infrastructure/Persistence/Migrations/`. The csproj embeds every `*.sql` there
  (`<EmbeddedResource Include="Persistence/Migrations/*.sql" />`), so nothing else needs registering.
- Name: `NNNN_<name>.sql`, the next number above the last file, four digits, the name in `[A-Za-z0-9_]`, for
  example `0003_add_task_labels.sql`. The number is the version; the runner applies pending versions in order and
  records each as `version name` (`3 add_task_labels`) in `__zaorm_migrations` under the source `daedalus`.
- Start with a comment saying what the migration does and why. Quote every identifier, as the existing tables are
  mixed case: `ALTER TABLE "Tasks" ADD COLUMN "Labels" text[] NOT NULL DEFAULT '{}';`.
- A new `NOT NULL` column on a table with rows needs a default or a backfill in the same file.
- Each migration runs in its own transaction with its history row, so a failing statement leaves nothing behind.

## 2. Map it

The SQL that reads and writes a table lives beside its map in `src/Daedalus.Infrastructure/Persistence/Orm/<Aggregate>/`:
the `[TableMap]` class, the row record and the `<Aggregate>Sql` class. Add a new column to the map, the row record
and every raw `[Command]` insert, which lists every column. Domain stays framework-free: no map attributes, entities
are rebuilt through their own `Rehydrate` factory, and invariants live in `Create`, returning a
`ZeroAlloc.Results` `Result<T>`.

## 3. Never

- Edit or renumber a file that has shipped. The runner keeps no checksum: an applied version is never run again,
  so an edit only reaches new databases and the two drift apart. Write a new migration instead.
- Insert, update or delete rows in `__zaorm_migrations` or `__EFMigrationsHistory` by hand. The EF history is frozen
  at `20261014090300_AddNodeSelectionUniqueIndex` and only read, to adopt an EF-built database as version 1.
- Rename the source `daedalus` (`DaedalusMigrations.SourceName`): the runner would run migration 1 again.
- Build a schema any other way, such as `EnsureCreated` or an ad-hoc `CREATE TABLE` in a test.

## 4. Test it

Every Postgres test fixture builds its schema with `DatabaseSchema.ApplyAsync` (`tests/Shared/DatabaseSchema.cs`):
the `vector` extension, the Daedalus migrations, then the workflow engine's, the same order as the host. A new file
therefore reaches every integration and Playwright test at once.

`tests/Daedalus.Tests.Integration/Migrations/` pins the chain:

- `BaselineAdoptionTests` and `MigrationsHostTests` assert the exact applied history (`"1 baseline"`,
  `"2 drop_dead_rowversions"`, ...): add the new version to their expected lists, and assert what the migration
  changed on an adopted EF database that holds rows.
- `BaselineSchemaEqualityTests.Fresh_database_matches_an_adopted_ef_database_after_every_migration` checks that a
  fresh database and an adopted one end with the same schema.
- Each store's `<Store>RoundTripTests` shows a changed column loads back whole.

```bash
dotnet test tests/Daedalus.Tests.Integration --filter "FullyQualifiedName~Daedalus.Tests.Integration.Migrations"
```

## 5. Apply

```bash
dotnet run --project src/Daedalus.Migrations   # what Aspire runs before the hosts: .WaitForCompletion(migrations)
```

`Daedalus.Migrations` opens one connection to `ConnectionStrings:daedalus`, runs `DaedalusMigrations.CreateRunner`,
then `WorkflowEngineMigrator` for the workflow engine's own sources. It exits 1 with the reason, before anything
runs, when a database's EF history ends anywhere but the frozen head.

If the local Postgres volume is stale (collation-version mismatch warnings, tables missing),
`docker volume rm daedalus_postgres_data` and let Aspire recreate it.
