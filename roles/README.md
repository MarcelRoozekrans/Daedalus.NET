# Squad role charters

Each file here (`<role>.md`, or `<role>/CHARTER.md`) is the versioned charter that `CharterSyncService`
syncs into the `RoleCharterVersions`/`RoleCharters` tables at host start and composes into an agent's
`Description`, `Instructions`, `Model` and `Skills` (see `CharteredAgentCatalog.Compose`). This file
itself is not a charter — it has no frontmatter, so a sync skips it (logged, harmlessly) rather than
failing.

A charter's frontmatter may not name a `tools:` key — `RoleCharterFileLoader` refuses the file at
sync if it does. A role's tool list is its security envelope and stays in
`src/Daedalus.Api/appsettings.json` under `Thalos:Agents`, where changing it needs a reviewed
deploy, not an edit to a markdown file in this folder. This is deliberate for `reviewer` in
particular: its independence from the agent whose work it reviews rests on holding no
write-capable `roslyn__*` tool, and that boundary must not move without a reviewed deploy.

(This note used to live as an HTML comment inside `reviewer.md`'s body. `Frontmatter` does not strip
comments out of the body — it is prose sent to the model verbatim — so the comment would have cost
real tokens on every reviewer turn for no benefit to the reviewer. It lives here instead.)
