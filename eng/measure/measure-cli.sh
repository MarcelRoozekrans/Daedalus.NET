#!/usr/bin/env bash
# Cold start and memory of the Daedalus CLI, run from a published binary or a DLL. Prints one JSON line on stdout.
# Usage: measure-cli.sh <published-binary-or-Daedalus.Cli.dll> [--runs N]
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
parse_runs "$@"
BIN="$(realpath "$TARGET")"
IDLE_SECONDS="${MEASURE_CLI_IDLE_SECONDS:-10}"
[ -f "$BIN" ] || die "$BIN not found"
WORK="$(mktemp -d)"
on_exit "rm -rf '$WORK'"

stack_up

# The CLI is a console pump with no ready prompt of its own, so "ready" is the first stdout line, and the harness
# makes that line the Generic Host's own: Microsoft.Hosting.Lifetime logs "Application started" once every hosted
# service has started. Every other category is set to None, so no warning from a hosted service can come first.
if [[ "$BIN" == *.dll ]]; then CMD=(dotnet "$BIN"); else CMD=("$BIN"); fi
# The containers run with --cpus 2; a host process gets the same two cores through taskset.
command -v taskset >/dev/null && CMD=(taskset -c "0,1" "${CMD[@]}")
cd "$(dirname "$BIN")"

starts=(); idles=(); peaks=()
for (( run=1; run<=RUNS; run++ )); do
  rm -f "$WORK/in" "$WORK/out"; mkfifo "$WORK/in"
  exec 4<>"$WORK/in"   # an open stdin that never yields a line and never reaches EOF
  t0="$(now_ms)"
  env "ConnectionStrings__daedalus=$PG_CONN" Logging__LogLevel__Default=None \
    "Logging__LogLevel__Microsoft.Hosting.Lifetime=Information" \
    "${CMD[@]}" <&4 >"$WORK/out" 2>"$WORK/err" &
  pid=$!
  ms=""
  limit=$(( $(date +%s) + 120 ))
  while [ "$(date +%s)" -lt "$limit" ]; do
    if [ -s "$WORK/out" ] && grep -q $'\n' "$WORK/out"; then ms=$(( $(now_ms) - t0 )); break; fi
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.02
  done
  if [ -z "$ms" ]; then
    echo "--- stdout" >&2; cat "$WORK/out" >&2; echo "--- stderr" >&2; tail -n 40 "$WORK/err" >&2
    kill "$pid" 2>/dev/null || true; exec 4>&-
    die "run $run: no stdout line from the CLI"
  fi
  head -n1 "$WORK/out" | grep -q 'Microsoft.Hosting.Lifetime' \
    || { echo "first stdout line: $(head -n1 "$WORK/out")" >&2; kill "$pid" 2>/dev/null || true; exec 4>&-; die "run $run: first stdout line is not the host's Application started"; }
  starts+=("$ms")
  sleep "$IDLE_SECONDS"
  idles+=("$(proc_mb "$pid" VmRSS)")
  peaks+=("$(proc_mb "$pid" VmHWM)")
  log "run $run/$RUNS: ready after ${ms} ms, rss ${idles[-1]} MiB, hwm ${peaks[-1]} MiB"
  kill "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
  exec 4>&-
done

digest="sha256:$(sha256sum "$BIN" | cut -d' ' -f1)"
app_mb="$(du -sk "$(dirname "$BIN")" | awk '{printf "%.1f\n", $1/1024}')"
emit_json cli "$(commit_short)" "$digest" "$RUNS" \
  "$(printf '%s\n' "${starts[@]}" | median)" "$(printf '%s\n' "${starts[@]}" | p90)" \
  "$(printf '%s\n' "${idles[@]}" | median)" "$(printf '%s\n' "${peaks[@]}" | median)" \
  null "$app_mb"
