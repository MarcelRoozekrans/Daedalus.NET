# Start-up, memory and size harness

Measures cold start, resident memory and size of the API image, the sandbox image and the CLI. Phase 3.1 records the
JIT numbers with it; phase 3.6 runs the same scripts, unchanged, for the Native AOT numbers.

```bash
eng/measure/measure-api.sh     <image>               [--runs N]   # default 10
eng/measure/measure-sandbox.sh <image>               [--runs N]
eng/measure/measure-cli.sh     <binary-or-dll>       [--runs N]
```

Each prints progress on stderr and one JSON line on stdout:

```json
{"target":"api","commit":"8173fd55","digest":"sha256:...","cpus":2,"runs":10,"startup_ms_median":2232.0,
 "startup_ms_p90":2400.0,"rss_idle_mb":191.2,"rss_peak_mb":203.2,"image_mb":463.7,"app_mb":104.5}
```

The scripts export `LC_ALL=C` so the numbers do not depend on the locale; the measured CLI inherits it.

A field that does not apply is `null`: `image_mb` for the CLI, nothing else today.

## Definitions

- **commit** is `git rev-parse --short=8 HEAD` of the checkout that holds the script, not of the image. Run the scripts
  from a worktree at the commit the image was built from.
- **digest** is the image ID, or for the CLI the sha256 of the file given.
- **cpus** is 2. Containers run with `--cpus 2`; the CLI runs under `taskset` on the first two allowed cores.
- **startup_ms** is the time from just before `docker run` (or the process spawn) to the ready signal. Median and p90
  by nearest rank, over `runs` cold starts. Every start is a new container, removed between runs.
- **rss_idle_mb** and **rss_peak_mb** are `VmRSS` and `VmHWM` of the container's main process, in MiB, read from
  `/proc/<pid>/status`. Each is the median over the runs.
- **image_mb** is `docker image inspect` `.Size`, in MiB. **app_mb** is `du -sk /app` inside the image in MiB, or for
  the CLI the size of the directory holding the binary.

## What each target does

| Target | Ready signal | Idle wait | Peak |
| --- | --- | --- | --- |
| API | first 200 from `GET /health`, polled every 50 ms | 60 s (`MEASURE_IDLE_SECONDS`), then `VmRSS` | `VmHWM` after `load-projects.sh` |
| Sandbox | first 200 from `GET /control/ready` with the bearer token, polled every 50 ms | 10 s (`MEASURE_SANDBOX_IDLE_SECONDS`) | `VmHWM` at the same moment |
| CLI | the first stdout line | 10 s (`MEASURE_CLI_IDLE_SECONDS`) | `VmHWM` at the same moment |

- **API.** The container runs with `--network host`, so the API reaches Keycloak as `localhost:8082`, which is the
  issuer of the tokens it validates, and Postgres on its published port. `ASPNETCORE_ENVIRONMENT=Development` is what
  allows the http Authority, as in the AppHost. Logging is set to the levels of `appsettings.json`, because
  `appsettings.Development.json` logs at Debug. `Thalos__Workflow__DataRoot` is set to `/tmp/...`, because the image
  has no `LOCALAPPDATA` and a blank data root then resolves to a relative path, which the API refuses at boot.
  The sandbox runtime finds no Docker socket in the container and logs a warning; the API still serves.
- **Load.** `load-projects.sh` sends 200 `GET /api/projects` at concurrency 10 and fails unless every reply is 2xx.
  The global rate limit is 100 requests a minute per signed-in subject, so one user gets 100 replies of 429. The
  requests alternate between the realm's `dev` and `admin` users, 100 each. The client secret and the passwords are
  read from `keycloak-realm.json` into variables and reach curl through stdin, never argv, never a file, never the log.
  The database holds the migrated schema and no rows, so the list is empty. Compare runs on the same state.
- **Sandbox.** The `THALOS_SANDBOX_*` variables are the keys of Thalos' `SandboxSettings`: `RUN_ID` (a fresh GUID),
  `TOKEN` (32 random bytes as base64url, fresh per run), `WRITE_EXTENSIONS` and `PROTECTED_PATHS`. No stack is needed.
- **CLI.** The CLI is a console pump and prints nothing when it is ready. The harness therefore sets the log level of
  every category to `None` except `Microsoft.Hosting.Lifetime`, whose "Application started" message the Generic Host
  logs after every hosted service has started. That is the first stdout line, and the script fails if the first line
  is anything else. Stdin is an open FIFO that never delivers a line, so the pump idles. The process runs in the
  directory of the binary, against the same Postgres. Pass a published binary, or `Daedalus.Cli.dll`, which runs with `dotnet`.

## The stack

`measure-api.sh` and `measure-cli.sh` start their own Postgres (`pgvector/pgvector:pg16`) and Keycloak
(`quay.io/keycloak/keycloak:26.1`, `start-dev --import-realm` of `keycloak-realm.json`) on a private network, then apply
the migrations with `dotnet run --project src/Daedalus.Migrations` of the checkout. The containers are
`daedalus-measure-pg` and `daedalus-measure-kc`, on `daedalus-measure-net`, with Postgres on 127.0.0.1:55432
(`MEASURE_PG_PORT`) and Keycloak on 127.0.0.1:8082 (`MEASURE_KC_PORT`).
`docker-compose.full.yml` is not used, because its `migrations` service builds `src/Daedalus.Migrations/Dockerfile`,
which does not exist, and it sets `ConnectionStrings__DefaultConnection` where the hosts read `ConnectionStrings:daedalus`.

A script that starts the stack removes it, with its volumes, when it exits. A stack that is already running is reused
and left alone, and `MEASURE_KEEP_STACK=1` leaves one the script started, so the three targets can share one.
Nothing outside those two containers and that network is touched.

## Requirements

`docker`, `jq`, `curl`, the .NET SDK of `global.json`, ports 8082 and 55432 free, and the image to measure built
beforehand, for example `docker build -f src/Daedalus.Api/Dockerfile -t <tag> .`.
