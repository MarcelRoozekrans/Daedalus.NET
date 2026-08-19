---
name: thalos-release
description: How to cut and publish a Thalos.NET release, and how to consume it from Daedalus.
tags: [release, nuget, thalos, ci]
---

# Cutting a Thalos.NET release

Repo: `C:\Projects\Prive\Thalos.NET` (GitHub `MarcelRoozekrans/Thalos.NET`). Full runbook:
`docs/release.md` in that repo. Rules that are easy to get wrong are repeated here.

- **No prereleases on nuget.org.** Only stable `X.Y.Z`, and only from the commit release-please tagged
  `vX.Y.Z`. The `publish-nuget` job refuses everything else.
- **GitVersion** derives the version from git history; `pack-validate` packs on every push and
  rehearses the nuget.org push against a local feed.
- **release-please** proposes releases from conventional commits (manifest mode, manual dispatch only).
- **Pre-1.0 bump rules:** a `feat:` bumps the **patch** (0.1.0 → 0.1.1); only `feat!:`/`BREAKING CHANGE`
  bumps the minor. A deliberate minor therefore needs an empty commit with a `Release-As:` footer.

## Steps

```bash
# 1. Deliberate version (e.g. a minor for a new package). Skip if the commits already imply it.
git commit --allow-empty -m "chore: set the release version" -m "Release-As: 0.3.0"
git push origin main

# 2. Open the release PR: release-please reads the conventional commits since the last release.
gh workflow run release-please.yml --ref main

# 3. Review and merge the "chore(main): release X.Y.Z" PR like any other PR.

# 4. Dispatch again: release-please now creates the GitHub release and the vX.Y.Z tag.
gh workflow run release-please.yml --ref main

# 5. Publish that exact commit. build-test (both OS) and pack-validate gate the push, and
#    publish-nuget refuses unless the checked-out commit is tagged vX.Y.Z.
gh workflow run ci.yml --ref vX.Y.Z -f publish_to_nuget=true
```

**Step 4 is the one that gets skipped.** A merged release PR looks exactly like a finished release: the
manifest says the new version and `CHANGELOG.md` has its entry, but without the second dispatch there is
no tag, no GitHub release and nothing on nuget.org. Verify with `git tag --list` against
`gh release list`, never by looking at the PR.

**Verifying the publish.** nuget.org takes several minutes to index. Immediately after a successful push
`dotnet package search` still reports the previous version and the flat container 404s — neither means the
push failed. Poll until it returns 200:

```bash
curl -s -o /dev/null -w "%{http_code}" https://api.nuget.org/v3-flatcontainer/thalos.net.skills/index.json
```

`pack-validate` checks the **package list and each package's TFMs**, so a new package must be added to
it in the same release: 0.2.0 shipped eight, 0.3.0 ships nine (`Thalos.NET.Skills` joined). Everything
except `Thalos.NET.Memory.RagNet` (net10.0-only) ships `net8.0` + `net10.0`.

## Consuming it from Daedalus before it is published

```powershell
pwsh C:\Projects\Prive\Thalos.NET\scripts\pack-local.ps1   # packs X.Y.Z-local.<timestamp> into C:\Projects\Prive\.nuget-local
```

Pin that exact version on every `Thalos.NET*` id in `Directory.Packages.props` and add the folder as a
`nuget.config` source with package-source mapping on `Thalos.NET*`. Two rules: **CI cannot see that
folder**, so either do not push, or commit the `.nupkg` files under `packages-local/` and use a relative
path; and the local pin, the source and the folder all go away before the PR is merged.

## Upgrading Daedalus to a new Thalos.NET

Bump every `Thalos.NET*` pin in `Directory.Packages.props` together — they ship as a set and mixing
versions is not supported. Watch for dependencies the library upgraded underneath you: 0.3.0 moved
`Thalos.NET.Testing` to AwesomeAssertions 9.5.0, and because central package management lets an explicit
pin win over a transitive one, Daedalus's older pin silently downgraded it and the contract base classes
failed to load at runtime with `FileNotFoundException`. The tell is that inherited contract facts fail
while locally-authored ones pass.
