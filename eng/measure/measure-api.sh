#!/usr/bin/env bash
# Cold start, idle and peak memory, and size of the API image. Prints one JSON line on stdout.
# Usage: measure-api.sh <image> [--runs N]      (see README.md for the definitions)
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
parse_runs "$@"
IMAGE="$TARGET"
IDLE_SECONDS="${MEASURE_IDLE_SECONDS:-60}"
CONTAINER="daedalus-measure-api"
API_PORT="${MEASURE_API_PORT:-18080}"
BASE="http://127.0.0.1:${API_PORT}"

docker image inspect "$IMAGE" >/dev/null 2>&1 || die "image $IMAGE not found"
stack_up
on_exit "docker rm -f -v $CONTAINER"
APP_MB="$(image_app_mb "$IMAGE")"

starts=(); idles=(); peaks=()
for (( run=1; run<=RUNS; run++ )); do
  docker rm -f -v "$CONTAINER" >/dev/null 2>&1 || true
  # Host networking, so the API reaches Keycloak as localhost:8082, the issuer of the tokens, and Postgres on its
  # published port. Development mirrors the AppHost and the compose file: it is what allows an http Authority.
  t0="$(now_ms)"
  docker run -d --name "$CONTAINER" --network host --cpus "$CPUS" \
    -e ASPNETCORE_ENVIRONMENT=Development \
    -e ASPNETCORE_URLS="http://127.0.0.1:${API_PORT}" \
    -e ConnectionStrings__daedalus="$PG_CONN" \
    -e Authentication__Authority="http://localhost:${KC_PORT}/realms/daedalus" \
    -e OTEL_EXPORTER_OTLP_ENDPOINT= \
    -e Logging__LogLevel__Default=Information -e Logging__LogLevel__Microsoft=Warning \
    -e Logging__LogLevel__Daedalus=Information \
    -e Thalos__Workflow__DataRoot=/tmp/daedalus-workflow-data \
    "$IMAGE" >/dev/null
  if ! ms="$(poll_200 "$BASE/health" "$t0" 120 "$CONTAINER")"; then
    docker logs --tail 40 "$CONTAINER" >&2 || true
    die "run $run: /health never returned 200"
  fi
  starts+=("$ms")
  log "run $run/$RUNS: first 200 after ${ms} ms"
  pid="$(docker inspect -f '{{.State.Pid}}' "$CONTAINER")"
  sleep "$IDLE_SECONDS"
  [ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER")" = "true" ] \
    || { docker logs --tail 40 "$CONTAINER" >&2 || true; die "run $run: the API exited during the idle wait"; }
  v="$(proc_mb "$pid" VmRSS)" || die "run $run: cannot read idle RSS"
  idles+=("$v")
  "$MEASURE_DIR/load-projects.sh" "$BASE" >&2 || { docker logs --tail 40 "$CONTAINER" >&2 || true; die "run $run: load failed"; }
  v="$(proc_mb "$pid" VmHWM)" || die "run $run: the API exited during the load"
  peaks+=("$v")
  log "run $run/$RUNS: idle ${idles[-1]} MiB, peak ${peaks[-1]} MiB"
  docker rm -f -v "$CONTAINER" >/dev/null
done

emit_json api "$(commit_short)" "$(image_digest "$IMAGE")" "$RUNS" \
  "$(printf '%s\n' "${starts[@]}" | median)" "$(printf '%s\n' "${starts[@]}" | p90)" \
  "$(printf '%s\n' "${idles[@]}" | median)" "$(printf '%s\n' "${peaks[@]}" | median)" \
  "$(image_mb "$IMAGE")" "$APP_MB"
