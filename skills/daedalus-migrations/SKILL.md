---
name: daedalus-migrations
description: How to add, verify and apply an EF Core migration in the Daedalus repo.
tags: [dotnet, ef, database, daedalus]
---

# Adding an EF Core migration

The model lives in `src/Daedalus.Infrastructure`; the startup project is `src/Daedalus.Api`
(it supplies the connection string and the DI graph the design-time factory needs).

## 1. Change the model first

- Aggregate: `src/Daedalus.Domain/Entities/<Name>.cs`. Domain stays framework-free — no Thalos, no EF
  attributes, invariants enforced in `Create`/`Update` returning `CSharpFunctionalExtensions.Result`.
- Mapping: `src/Daedalus.Infrastructure/Persistence/Configurations/<Name>Configuration.cs`,
  `internal sealed class … : IEntityTypeConfiguration<T>`. It is picked up automatically by
  `ApplyConfigurationsFromAssembly`. Use the aggregate's `Max*Length` constants for `HasMaxLength`, so a
  violation is a validation error and never a varchar failure.
- `DbSet`: add `public DbSet<T> Xs => Set<T>();` to `ApplicationDbContext`.

## 2. Scaffold

```powershell
dotnet ef migrations add <Name> --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api --output-dir Migrations
```

This writes three things: the migration, its `.Designer.cs` and an updated
`ApplicationDbContextModelSnapshot.cs`. Never hand-edit the snapshot to "fix" a diff — EF 10 throws on
`Migrate()` when the model has pending changes that are not in the snapshot, so a hand-patched snapshot
fails at runtime, not at build time.

## 3. Read the generated file before you trust it

- **The scaffolder diffs both directions.** `Down` is generated from the snapshot, so if a mapping was
  removed in the same change (a type, a column type, a provider), the down direction can crash the
  scaffolder with a `NullReferenceException` in `MigrationsModelDiffer` — or, worse, generate a `Down`
  that leaves a schema the *previous* migration's `Down` cannot run against. Walk the chain mentally:
  after this `Down`, can the predecessor's `Down` still run? `AddAgentMemories` had to hand-add an
  `ALTER TABLE … ADD COLUMN IF NOT EXISTS "Embedding" vector(384);` for exactly this reason.
- **Order the operations yourself** when data moves. EF emits `DropTable` first; a copy has to run
  after the `CreateTable` and its indexes, and before the drop.
- Add the `#pragma warning disable CA1861` header if the file contains `new[]` column arrays
  (composite indexes), give the class and `Down` XML doc comments saying what a rollback destroys, and
  strip the BOM (`dotnet format` enforces CHARSET). The scaffolder writes one; nothing else in
  `Migrations/` has one.

## 4. Test it

Migrations get an integration test under `tests/Daedalus.Tests.Integration/Migrations/`. The pattern:
create a throwaway database, `MigrateAsync(<predecessor>)`, seed, `MigrateAsync()`, assert — and a
second fact that rolls the chain back past this migration and forward again. Rollback failures are
silent and destructive, so they are pinned permanently rather than checked once by hand.

```powershell
dotnet test tests/Daedalus.Tests.Integration --nologo --filter "FullyQualifiedName~<Name>MigrationTests"
```

Note the integration fixture itself uses `EnsureCreatedAsync()`, not migrations: a new `DbSet` is
available to store/contract tests before its migration exists. The flip side is that between adding the
`DbSet` and scaffolding the migration, every test that calls `MigrateAsync()` fails with EF 10's
`PendingModelChangesWarning` — expected, and closed by step 2.

## 5. Apply

```powershell
dotnet run --project src/Daedalus.Migrations          # what Aspire runs, with .WaitForCompletion(migrations)
dotnet ef database update --project src/Daedalus.Infrastructure --startup-project src/Daedalus.Api
```

If the local Postgres volume is stale (collation-version mismatch warnings, tables missing),
`docker volume rm daedalus_postgres_data` and let Aspire recreate it.
