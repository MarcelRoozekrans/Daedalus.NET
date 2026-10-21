#!/usr/bin/env bash
# Cold start, memory and size of the sandbox image. Prints one JSON line on stdout.
# Usage: measure-sandbox.sh <image> [--runs N]
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
parse_runs "$@"
IMAGE="$TARGET"
IDLE_SECONDS="${MEASURE_SANDBOX_IDLE_SECONDS:-10}"
CONTAINER="daedalus-measure-sandbox"

command -v docker >/dev/null || die "docker is required"
command -v jq >/dev/null || die "jq is required"
docker image inspect "$IMAGE" >/dev/null 2>&1 || die "image $IMAGE not found"
on_exit "docker rm -f -v $CONTAINER"
image_app_mb "$IMAGE"   # sets APP_MB, in this shell

starts=(); idles=(); peaks=()
for (( run=1; run<=RUNS; run++ )); do
  docker rm -f -v "$CONTAINER" >/dev/null 2>&1 || true
  # A fresh random bearer token and run id per run; the variables are the keys of Thalos' SandboxSettings.
  token="$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')"
  run_id="$(cat /proc/sys/kernel/random/uuid)"
  t0="$(now_ms)"
  # The token is set for this one command only; `-e NAME` with no value copies it, so it is never on argv.
  THALOS_SANDBOX_TOKEN="$token" docker run -d --name "$CONTAINER" --cpus "$CPUS" -p 127.0.0.1::8080 \
    -e THALOS_SANDBOX_RUN_ID="$run_id" \
    -e THALOS_SANDBOX_TOKEN \
    -e THALOS_SANDBOX_WRITE_EXTENSIONS='.cs;.md' \
    -e THALOS_SANDBOX_PROTECTED_PATHS='.git/;.github/' \
    "$IMAGE" >/dev/null
  port="$(docker port "$CONTAINER" 8080/tcp | head -n1 | sed 's/.*://')"
  # The bearer header reaches curl through stdin, never argv.
  limit=$(( $(date +%s) + 120 )); ms=""
  while [ "$(date +%s)" -lt "$limit" ]; do
    code="$(printf 'header = "Authorization: Bearer %s"\n' "$token" \
      | curl -s -o /dev/null -w '%{http_code}' --max-time 1 -K - "http://127.0.0.1:${port}/control/ready" 2>/dev/null || true)"
    if [ "$code" = "200" ]; then ms=$(( $(now_ms) - t0 )); break; fi
    [ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER")" = "true" ] || break
    sleep 0.05
  done
  if [ -z "$ms" ]; then docker logs --tail 40 "$CONTAINER" >&2 || true; die "run $run: /control/ready never returned 200"; fi
  starts+=("$ms")
  pid="$(docker inspect -f '{{.State.Pid}}' "$CONTAINER")"
  sleep "$IDLE_SECONDS"
  v="$(proc_mb "$pid" VmRSS)" || die "run $run: sandbox container exited during the idle wait"
  idles+=("$v")
  v="$(proc_mb "$pid" VmHWM)" || die "run $run: sandbox container exited during the idle wait"
  peaks+=("$v")
  log "run $run/$RUNS: ready after ${ms} ms, idle ${idles[-1]} MiB, peak ${peaks[-1]} MiB"
  docker rm -f -v "$CONTAINER" >/dev/null
done
unset token

emit_json sandbox "$(commit_short)" "$(image_digest "$IMAGE")" "$RUNS" \
  "$(printf '%s\n' "${starts[@]}" | median)" "$(printf '%s\n' "${starts[@]}" | p90)" \
  "$(printf '%s\n' "${idles[@]}" | median)" "$(printf '%s\n' "${peaks[@]}" | median)" \
  "$(image_mb "$IMAGE")" "$APP_MB"
