# Squad role charters

Each file here (`<role>.md`, or `<role>/CHARTER.md`) is the versioned charter that `CharterSyncService`
syncs into the `RoleCharterVersions`/`RoleCharters` tables at host start and composes into an agent's
`Description`, `Instructions`, `Model` and `Skills` (see `CharteredAgentCatalog.Compose`). This file
itself is not a charter — it has no frontmatter, so it is excluded from the `Content` item that copies
this folder next to every host (`Daedalus.Api.csproj`/`Daedalus.Cli.csproj`), rather than being copied,
scanned and skipped (logged) on every start.

A charter's frontmatter may not name a `tools:` key — `RoleCharterFileLoader` refuses the file at
sync if it does. A role's tool list is its security envelope and stays in
`src/Daedalus.Api/appsettings.json` under `Thalos:Agents`, where changing it needs a reviewed
deploy, not an edit to a markdown file in this folder. This is deliberate for `reviewer` in
particular: its independence from the agent whose work it reviews rests on holding no
write-capable `roslyn__*` tool, and that boundary must not move without a reviewed deploy.

(This note used to live as an HTML comment inside `reviewer.md`'s body. `Frontmatter` does not strip
comments out of the body — it is prose sent to the model verbatim — so the comment would have cost
real tokens on every reviewer turn for no benefit to the reviewer. It lives here instead.)

Every host that copies `skills/**` copies `roles/**` too, `Daedalus.Cli` included — and
`CharterSyncService` syncs unconditionally, not only when an `AgentEnvelope` names a role. So
`Daedalus.Cli`, which declares no `"Chartered": true` agent today, still upserts `implementer` and
`reviewer` into the shared `RoleCharterVersions`/`RoleCharters` tables and runs the deactivation sweep
on every start — the same Postgres tables `Daedalus.Api`'s own sync writes. A version with no matching
envelope simply composes into no agent on that host.

## Why the reviewer runs `claude-opus-5`

Design decision D12 asks for a peer-strength model from a different family so the reviewer does not
share the implementer's blind spots. Only one chat provider is configured in this repo (the
second-provider work is deliberately parked, see `docs/planning/parked-ideas.md`), so a different
vendor is not available today — every option shares training lineage. `claude-opus-5` is the best
available approximation: a different model line from Sonnet 5, not a point release of it, and
peer-or-better strength. This is a different model line, not a different provider, so the
uncorrelated-blind-spots argument is weakened, not satisfied.

## Why the reviewer's skills are enumerated, not `*`

`reviewer.md` names `manufacture-review` and `manufacture-retrospect` explicitly rather than granting
every skill with `*`. `Daedalus Architect` is the only agent whose `Skills` is `*`; a reviewer that
could load `manufacture-implement` could read the instructions the work it judges was produced from.
