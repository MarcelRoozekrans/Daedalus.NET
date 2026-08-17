# Versioning and releases

Same design as [Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET) and
[Thalos.NET](https://github.com/MarcelRoozekrans/Thalos.NET), adapted for an application that ships container
images instead of NuGet packages.

- **No prereleases.** ghcr.io only ever receives `X.Y.Z` / `X.Y` / `latest` tags from the commit
  release-please tagged `vX.Y.Z`. `publish-release` refuses everything else. Every push to `main` still
  publishes the moving tip as `<sha>` and `main` — that is not a release.
- **GitVersion** (`GitVersion.yml`, dotnet local tool pinned in `.config/dotnet-tools.json`) derives every
  build's version from git history. `main` carries no label, so it derives plain stable numbers (`0.1.0`
  until the first tag, then the tag's version bumped per GitHubFlow); the commit tagged `vX.Y.Z` derives
  exactly `X.Y.Z`; other branches derive `X.Y.Z-<branch>.N`. The `version` job in `ci.yml` derives it once;
  `build-and-test` passes it as `-p:Version`, the Docker jobs as the `APP_VERSION` build-arg (each
  Dockerfile forwards it to `dotnet build`/`publish`), so `AssemblyInformationalVersion` and the
  `org.opencontainers.image.version` label agree with the git tag.
- **release-please** (`.github/workflows/release-please.yml`, manifest mode:
  `release-please-config.json` + `.release-please-manifest.json`) proposes releases from conventional
  commits and cuts the tag. Manual dispatch only.
- **Conventional commits** are enforced on pull requests by the `commitlint` job (`.commitlintrc.yml`).
- **Publishing** is the `publish-release` job in `ci.yml`: manual dispatch with `publish_release=true`,
  `GITHUB_TOKEN` with `packages: write` (no extra secret), gated on `build-and-test` and the three
  `docker` builds being green on the same commit.

## One-time setup

```bash
# GitHub Actions must be allowed to open the release PR (Settings → Actions → General →
# "Allow GitHub Actions to create and approve pull requests"), or via the API:
gh api -X PUT repos/MarcelRoozekrans/Daedalus.NET/actions/permissions/workflow \
  -f default_workflow_permissions=read -F can_approve_pull_request_reviews=true
```

The container packages appear under https://github.com/MarcelRoozekrans?tab=packages after the first push;
make them public there if the images should be pullable without a token.

## Cutting a release

```bash
# 1. First release only: release-please proposes 1.0.0 by default. Override with an empty commit
#    carrying a Release-As footer before the first dispatch.
git commit --allow-empty -m "chore: set the first release version" -m "Release-As: 0.1.0"
git push origin main

# 2. Open the release PR — release-please reads the conventional commits since the last release
#    and proposes the version they imply (CHANGELOG.md + version.txt on the PR branch).
gh workflow run release-please.yml --ref main

# 3. Review and merge the release PR ("chore(main): release X.Y.Z"), like every PR.

# 4. Dispatch again: release-please sees the merged release PR and creates the GitHub release and
#    the vX.Y.Z tag — the tag the publish gate checks.
gh workflow run release-please.yml --ref main

# 5. Publish that exact commit: dispatch CI on the release tag with the publish input. version,
#    build-and-test and the three docker builds run first; publish-release refuses to start until they
#    are green, and refuses to push unless the checked-out commit is tagged vX.Y.Z (a prerelease or an
#    untagged commit fails the gate). `--ref main` also works while main still points at the release commit.
gh workflow run ci.yml --ref vX.Y.Z -f publish_release=true
```

Pre-1.0 bump rules (`release-please-config.json`): a `feat!:`/`BREAKING CHANGE` bumps the minor
(0.1.0 → 0.2.0), a `feat:` bumps the patch (0.1.0 → 0.1.1). Once 1.0.0 is cut those become
major/minor as usual.

## Relationship to project milestones

`docs/planning/` (project-orchestration) tracks milestones and phases; releases are independent of them.
A milestone may span several releases, and `complete-milestone`'s tag convention (`vN.0`) is superseded by
release-please's `vX.Y.Z` — do not create milestone tags by hand, cut a release instead.

## Renovate

`.github/renovate.json` groups and automerges dependency PRs; they go through the same CI gates and their
commit messages are conventional (`:semanticCommits`), so they appear in the CHANGELOG under the type
Renovate uses (`chore(deps)`, filtered out of the visible sections by release-please's defaults).
