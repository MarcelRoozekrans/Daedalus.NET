# Project Conventions

> Written by `init-conventions`. Do not hand-edit — re-run the sub-skill instead; the Commit & Release Protocol reads these fields.

**Established:** 2026-08-19

## Stack

**Language / runtime:** .NET 10 (net10.0), C# 13
**Package manager:** NuGet, central package management enabled — `Directory.Packages.props` sets `ManagePackageVersionsCentrally=true` and no `PackageReference` in `src/` carries a `Version` attribute
**Framework:** ASP.NET Core, Blazor WASM, .NET Aspire 13.1
**Datastore:** PostgreSQL 16 + pgvector

## Commits

**Format:** conventional
**Scopes:** free
**Scope source:** n/a
**Fallback when scope not allowed:** omit scope

## Branching

**Model:** feature-branch
**PR required:** yes
**Protected branches:** main — active branch ruleset named `Main` on `~DEFAULT_BRANCH`, rules: deletion, non_fast_forward, required_status_checks, pull_request (1 approval; squash or rebase only); the admin bypass applies only when merging a pull request (2026-10-04)

## Versioning & Release

**Scheme:** semver
**Released by:** release-please
**Milestone completion tags a release:** no
**Changelog:** auto

## Deployment

**Deploy target:** ghcr.io/marcelroozekrans — daedalus-api, daedalus-console, daedalus-web
**Environments:** none
**Deployed by:** GitHub Actions ci.yml, gated publish-release job (workflow_dispatch with publish_release=true, on main or a v* tag)
