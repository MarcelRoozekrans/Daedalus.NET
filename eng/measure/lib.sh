#!/usr/bin/env bash
# Shared helpers for the measure-*.sh scripts. Sourced, never run. See README.md.
# shellcheck shell=bash

export LC_ALL=C   # sort -n and awk printf %.1f must not depend on the caller's locale

MEASURE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$MEASURE_DIR/../.." && pwd)"

CPUS=2
KC_PORT="${MEASURE_KC_PORT:-8082}"
PG_PORT="${MEASURE_PG_PORT:-55432}"
KC_IMAGE="quay.io/keycloak/keycloak:26.1"
PG_IMAGE="pgvector/pgvector:pg16"
NET_NAME="daedalus-measure-net"
PG_NAME="daedalus-measure-pg"
KC_NAME="daedalus-measure-kc"
REALM_FILE="$REPO_ROOT/keycloak-realm.json"
# Npgsql connection string for a process on the host and for a --network host container.
PG_CONN="Host=127.0.0.1;Port=${PG_PORT};Database=daedalus;Username=daedalus;Password=daedalus"

STACK_STARTED=0
CLEANUP_CMDS=()

die() { echo "error: $*" >&2; exit 2; }
log() { echo "[measure] $*" >&2; }

now_ms() { echo $(( $(date +%s%N) / 1000000 )); }

# Registers a command to run at exit, newest first. Safe to call several times.
on_exit() { CLEANUP_CMDS+=("$1"); }
run_cleanups() {
  local i
  for (( i=${#CLEANUP_CMDS[@]}-1; i>=0; i-- )); do eval "${CLEANUP_CMDS[$i]}" >/dev/null 2>&1 || true; done
}
trap run_cleanups EXIT
trap 'exit 130' INT TERM

parse_runs() {
  RUNS=10
  TARGET=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --runs) RUNS="${2:-}"; shift 2 ;;
      -*) die "unknown option $1" ;;
      *) [ -z "$TARGET" ] || die "unexpected argument $1"; TARGET="$1"; shift ;;
    esac
  done
  [ -n "$TARGET" ] || die "usage: $0 <image-or-binary> [--runs N]"
  [[ "$RUNS" =~ ^[1-9][0-9]*$ ]] || die "--runs needs a positive integer"
}

# median / p90 (nearest rank) of the numbers on stdin; prints null for no input. One decimal.
median() { sort -n | awk '{a[NR]=$1} END{ if(!NR){print "null"; exit} i=int((NR+1)/2); v=(NR%2)?a[i]:(a[NR/2]+a[NR/2+1])/2; printf "%.1f\n", v }'; }
p90()    { sort -n | awk '{a[NR]=$1} END{ if(!NR){print "null"; exit} i=int(0.9*NR); if(i<0.9*NR)i++; if(i<1)i=1; printf "%.1f\n", a[i] }'; }

# VmRSS / VmHWM of a pid, in MiB, one decimal. Fails, with a message, when the value cannot be read, so a caller
# that writes `v="$(proc_mb ...)" || exit` never records an empty number.
proc_mb() { # <pid> <VmRSS|VmHWM>
  local v
  v="$(awk -v k="$2:" '$1==k {printf "%.1f\n", $2/1024}' "/proc/$1/status" 2>/dev/null)"
  if [ -z "$v" ]; then echo "error: cannot read $2 of pid $1; the process has exited" >&2; return 1; fi
  echo "$v"
}

commit_short() { git -C "$REPO_ROOT" rev-parse --short=8 HEAD 2>/dev/null || echo null; }

# json_str <value|""> prints a quoted JSON string, or null for empty.
json_str() { if [ -z "$1" ]; then echo null; else jq -Rn --arg v "$1" '$v'; fi; }

# emit_json target commit digest runs startup_median startup_p90 rss_idle rss_peak image_mb app_mb
emit_json() {
  jq -cn --arg target "$1" --arg commit "$2" --arg digest "$3" --argjson cpus "$CPUS" --argjson runs "$4" \
    --argjson sm "$5" --argjson sp "$6" --argjson ri "$7" --argjson rp "$8" --argjson im "$9" --argjson am "${10}" \
    '{target:$target, commit:$commit, digest:(if $digest=="" then null else $digest end), cpus:$cpus, runs:$runs,
      startup_ms_median:$sm, startup_ms_p90:$sp, rss_idle_mb:$ri, rss_peak_mb:$rp, image_mb:$im, app_mb:$am}'
}

image_digest() { docker image inspect --format '{{.Id}}' "$1"; }
image_mb()     { docker image inspect --format '{{.Size}}' "$1" | awk '{printf "%.1f\n", $1/1048576}'; }
# /app size inside an image, MiB (du -sk), left in the global APP_MB. It sets a global instead of printing, so the
# caller runs it in its own shell and the on_exit registrations below survive a Ctrl-C. Runs du in the image; an image
# without du (a chiseled one) falls back to copying /app out of a created container and measuring it on the host.
image_app_mb() {
  local kb tmp cid
  APP_MB=""
  if kb="$(docker run --rm --entrypoint du "$1" -sk /app 2>/dev/null | awk '{print $1}')" && [ -n "$kb" ]; then
    APP_MB="$(awk -v k="$kb" 'BEGIN{printf "%.1f\n", k/1024}')"; return 0
  fi
  tmp="$(mktemp -d)"
  on_exit "rm -rf '$tmp'"
  cid="$(docker create "$1")" || die "cannot create a container from $1"
  on_exit "docker rm -f -v $cid"
  if docker cp "$cid:/app" "$tmp/app" >/dev/null 2>&1; then kb="$(du -sk "$tmp/app" | awk '{print $1}')"; else kb=""; fi
  docker rm -f -v "$cid" >/dev/null 2>&1 || true
  rm -rf "$tmp"
  [ -n "$kb" ] || die "cannot measure /app in $1"
  # shellcheck disable=SC2034 # read by the measure scripts that source this file
  APP_MB="$(awk -v k="$kb" 'BEGIN{printf "%.1f\n", k/1024}')"
}

# Waits for a URL to answer 200, polling every 50 ms. Prints elapsed ms since $2 (a now_ms value).
# Returns 1 on timeout (seconds, $3) or when container $4 (optional) has exited.
poll_200() { # <url> <t0_ms> <timeout_s> [container]
  local url="$1" t0="$2" limit=$(( $(date +%s) + $3 )) code
  while [ "$(date +%s)" -lt "$limit" ]; do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 1 "$url" 2>/dev/null || true)"
    if [ "$code" = "200" ]; then echo $(( $(now_ms) - t0 )); return 0; fi
    if [ -n "${4:-}" ] && [ "$(docker inspect -f '{{.State.Running}}' "$4" 2>/dev/null)" != "true" ]; then return 1; fi
    sleep 0.05
  done
  return 1
}

wait_for_url() { # <url> <timeout_s> <what>
  local limit=$(( $(date +%s) + $2 ))
  while [ "$(date +%s)" -lt "$limit" ]; do
    [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$1" 2>/dev/null || true)" = "200" ] && return 0
    sleep 1
  done
  die "$3 did not become ready within $2 s"
}

# --- the Postgres, Keycloak and migrations stack ---------------------------------------------------------------
# docker-compose.full.yml cannot be used as is: its migrations service points at src/Daedalus.Migrations/Dockerfile,
# which does not exist, and it sets ConnectionStrings__DefaultConnection where the hosts read ConnectionStrings:daedalus.
# So the harness starts the same images itself, on a private network, under names no other stack uses.
stack_up() {
  command -v docker >/dev/null || die "docker is required"
  command -v jq >/dev/null || die "jq is required"
  command -v curl >/dev/null || die "curl is required"
  if [ "$(docker inspect -f '{{.State.Running}}' "$PG_NAME" 2>/dev/null)" = "true" ] \
     && [ "$(docker inspect -f '{{.State.Running}}' "$KC_NAME" 2>/dev/null)" = "true" ]; then
    log "reusing the running measure stack"
    return 0
  fi
  # Half a stack, or a stopped one, is left over from an interrupted run. Both names are this harness's own, so
  # clear them rather than collide on the name or later delete a survivor we did not start.
  if docker inspect "$PG_NAME" >/dev/null 2>&1 || docker inspect "$KC_NAME" >/dev/null 2>&1; then
    log "removing a leftover, incomplete measure stack"
    docker rm -f -v "$PG_NAME" "$KC_NAME" >/dev/null 2>&1 || true
  fi
  STACK_STARTED=1
  on_exit stack_down
  docker network inspect "$NET_NAME" >/dev/null 2>&1 || docker network create "$NET_NAME" >/dev/null
  log "starting postgres and keycloak"
  docker run -d --name "$PG_NAME" --network "$NET_NAME" -p "127.0.0.1:${PG_PORT}:5432" \
    -e POSTGRES_USER=daedalus -e POSTGRES_PASSWORD=daedalus -e POSTGRES_DB=daedalus "$PG_IMAGE" >/dev/null
  docker run -d --name "$KC_NAME" --network "$NET_NAME" -p "127.0.0.1:${KC_PORT}:8080" \
    -v "$REALM_FILE:/opt/keycloak/data/import/realm.json:ro" \
    -e KEYCLOAK_ADMIN=admin -e KEYCLOAK_ADMIN_PASSWORD=changeme \
    "$KC_IMAGE" start-dev --import-realm >/dev/null
  local limit=$(( $(date +%s) + 90 ))
  until docker exec "$PG_NAME" pg_isready -h 127.0.0.1 -U daedalus -d daedalus >/dev/null 2>&1; do
    [ "$(date +%s)" -lt "$limit" ] || die "postgres did not become ready"
    sleep 1
  done
  log "applying migrations"
  local mlog
  mlog="$(mktemp)"
  on_exit "rm -f '$mlog'"
  ConnectionStrings__daedalus="$PG_CONN" dotnet run --project "$REPO_ROOT/src/Daedalus.Migrations" -c Release >"$mlog" 2>&1 \
    || { tail -n 40 "$mlog" >&2; rm -f "$mlog"; die "migrations failed"; }
  rm -f "$mlog"
  wait_for_url "http://localhost:${KC_PORT}/realms/daedalus" 180 keycloak
}

stack_down() {
  [ "$STACK_STARTED" = "1" ] || return 0
  [ "${MEASURE_KEEP_STACK:-0}" = "1" ] && { log "leaving the measure stack running"; return 0; }
  docker rm -f -v "$PG_NAME" "$KC_NAME" >/dev/null 2>&1 || true
  docker network rm "$NET_NAME" >/dev/null 2>&1 || true
}
