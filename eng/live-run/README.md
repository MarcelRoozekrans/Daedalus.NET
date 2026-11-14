# Live native run

One manufacture run on the natively published Api, issue #452. The `Live native run` workflow
(`.github/workflows/live-native-run.yml`) runs it on a GitHub runner, so no Native AOT publish happens on the shared VM.

## What the workflow does

1. Publishes `Daedalus.Api` and `Daedalus.Migrations` with Native AOT.
2. Starts Postgres as a service container and Keycloak with `keycloak-realm.json`, on `localhost:8082`.
3. Builds `daedalus-sandbox:dev` from `src/Daedalus.Sandbox/Dockerfile`, as the AppHost does.
4. Applies the migrations with the native Migrations binary.
5. Starts the native Api with `Thalos__Workflow__Sandbox__Enabled=true` on the runner's Docker.
6. Runs `drive.sh`, then uploads the Api log, the Keycloak and sandbox container logs, `run.json` and `cost.json`.

## Modes

| Mode | How | Proves | Spends |
|---|---|---|---|
| Dry | Dispatch with `dry_run` ticked, the default, or any pull request that changes the workflow or this folder | The native Api boots in sandbox mode; Keycloak sign-in; project and task creation; Manufacture refuses a repository that is not allow-listed with 422 | Nothing, uses no secret |
| Live | Dispatch with `dry_run` cleared | All of the above, then Manufacture, the sandboxed run, approval at the `gate` node, publish and the pull request on `daedalus-sandbox`. The job summary shows the run id, status, pull request and cost | Anthropic credit and one pull request |

The driver approves the run at its gate as a person would on the run page, with `human_approval` and without
applying the standing-instructions proposal. The live run fails the job unless the run ends `Succeeded` with a
pull request.

## Configuration it relies on

- The allow-list is `Thalos:Workflow:Repositories` in `src/Daedalus.Api/appsettings.json`: one entry, `sandbox`,
  for `https://github.com/MarcelRoozekrans/daedalus-sandbox.git`. The project's `RepositoryUrl` must match it
  (`TaskManufactureService.MatchRepository`).
- The mirror clone, the push and the pull request use `GITHUB_TOKEN` (`GitHubTokenSource`). `daedalus-sandbox` is
  private, so the token must read it too.
- The model provider reads `ANTHROPIC_API_KEY`.
- No Ollama: embeddings stay off, as `ConnectionStrings:ollama` is unset.

## Secrets and environment

A live dispatch runs in the `live-manufacture` environment and reads two environment secrets:

| Secret | Value |
|---|---|
| `ANTHROPIC_API_KEY` | An Anthropic API key |
| `SANDBOX_GITHUB_TOKEN` | A fine-grained token for `daedalus-sandbox` only: Contents read and write, Pull requests read and write |

Give the environment a required reviewer, so every live run waits for an approval. A dry run never enters the
environment and is never given either secret.

## Running the driver by hand

Against any Api whose Keycloak is on `localhost:$KC_PORT`:

```bash
KC_PORT=8082 DRY_RUN=true eng/live-run/drive.sh http://127.0.0.1:5010
```

`drive.sh` documents its other variables at the top.
