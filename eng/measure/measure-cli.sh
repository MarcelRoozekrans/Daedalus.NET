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
# Cores 0 and 1 may be outside the cores this shell may use, so pin to the first two allowed ones.
if command -v taskset >/dev/null; then
  allowed="$(taskset -pc $$ | sed 's/.*: //')"
  cores="$(tr ',' '\n' <<<"$allowed" \
    | awk -F- '{ e = ($2 == "" ? $1 : $2); for (i = $1; i <= e; i++) { print i; if (++n == 2) exit } }' | paste -sd, -)"
  [ -n "$cores" ] || die "cannot read the allowed cores from taskset"
  [[ "$cores" == *,* ]] || log "only one core is allowed ($cores); the CLI gets less than the 2 CPUs of the containers"
  CMD=(taskset -c "$cores" "${CMD[@]}")
fi
cd "$(dirname "$BIN")"

# The pid of the CLI while it is running; cleared once the loop has killed and reaped it, so the exit hook never
# signals a pid that may by then belong to another process.
CLI_PID=""
kill_cli() { [ -z "$CLI_PID" ] || kill "$CLI_PID"; }
on_exit kill_cli

starts=(); idles=(); peaks=()
for (( run=1; run<=RUNS; run++ )); do
  rm -f "$WORK/in" "$WORK/out"; mkfifo "$WORK/in"
  : >"$WORK/out"   # exists before the child starts, so the first poll can read it
  exec 4<>"$WORK/in"   # an open stdin that never yields a line and never reaches EOF
  t0="$(now_ms)"
  env "ConnectionStrings__daedalus=$PG_CONN" Logging__LogLevel__Default=None \
    "Logging__LogLevel__Microsoft.Hosting.Lifetime=Information" \
    "${CMD[@]}" <&4 >"$WORK/out" 2>"$WORK/err" &
  pid=$!
  CLI_PID="$pid"
  ms=""
  limit=$(( $(date +%s) + 120 ))
  while [ "$(date +%s)" -lt "$limit" ]; do
    if [ "$(wc -l <"$WORK/out")" -ge 1 ]; then ms=$(( $(now_ms) - t0 )); break; fi
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
  v="$(proc_mb "$pid" VmRSS)" || die "run $run: the CLI exited during the idle wait"
  idles+=("$v")
  v="$(proc_mb "$pid" VmHWM)" || die "run $run: the CLI exited before its peak RSS was read"
  peaks+=("$v")
  log "run $run/$RUNS: ready after ${ms} ms, rss ${idles[-1]} MiB, hwm ${peaks[-1]} MiB"
  kill "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
  CLI_PID=""
  exec 4>&-
done

digest="sha256:$(sha256sum "$BIN" | cut -d' ' -f1)"
app_mb="$(du -sk "$(dirname "$BIN")" | awk '{printf "%.1f\n", $1/1024}')"
emit_json cli "$(commit_short)" "$digest" "$RUNS" \
  "$(printf '%s\n' "${starts[@]}" | median)" "$(printf '%s\n' "${starts[@]}" | p90)" \
  "$(printf '%s\n' "${idles[@]}" | median)" "$(printf '%s\n' "${peaks[@]}" | median)" \
  null "$app_mb"
