#!/usr/bin/env bash
# Counts each target's distinct trim and AOT warnings (IL2xxx and IL3xxx) from a real native publish,
# and ratchets them against eng/aot/baseline.json in both directions.
#
# Usage: eng/aot/count-warnings.sh [--target <Name>|--all] [--check] [--write]
#   --target <Name>  one of Api, Cli, Migrations, Sandbox, Web. The default is --all.
#   --check          exit 1 when any count differs from the baseline, above or below it.
#   --write          write the counts into the baseline, keeping the targets not counted.
#
# Exit codes: 0 ok, 1 ratchet failure, 2 publish failure or bad usage.
# Lowering a baseline after fixing warnings: run with --target <Name> --write and commit
# eng/aot/baseline.json in the same change; --check fails on a fall that is not recorded.
#
# Distinct means deduplicated by code|file|line|member:
#   - file is repo-relative, line drops the column, so ILC's `File.cs(5)`, ILLink's `File.cs(5,13)` and
#     the Roslyn analyzer's `File.cs(5,27)` report of the same site are one warning;
#   - member is what the warning names. A warning with a location keeps its message, which names the
#     member it uses; a warning without one keeps `Origin(): message`, because ILC prints package code
#     without a location and only the used member tells MapGet from MapPost in the same method.
#   MSBuild repeats every warning in its end-of-build summary; the dedup removes those repeats too.
#
# Requirements: the .NET SDK from global.json, clang and zlib headers for the native targets, the
# wasm-tools workload for Web, and jq.
# Environment: AOT_BASELINE (default eng/aot/baseline.json), AOT_LOG_DIR (publish logs and the
# normalised keys per target; default $TMPDIR/daedalus-aot).
set -euo pipefail
# Byte-wise collation, so the sort -u dedup and the .keys files are the same on every machine.
export LC_ALL=C

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
baseline="${AOT_BASELINE:-$repo/eng/aot/baseline.json}"
log_dir="${AOT_LOG_DIR:-${TMPDIR:-/tmp}/daedalus-aot}"
all_targets=(Api Cli Migrations Sandbox Web)

usage() {
  sed -n '5,9p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

targets=()
check=false
write=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --target)
      [[ $# -ge 2 ]] || usage
      targets+=("$2")
      shift 2
      ;;
    --all) targets=("${all_targets[@]}"); shift ;;
    --check) check=true; shift ;;
    --write) write=true; shift ;;
    -h | --help) usage ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done
[[ ${#targets[@]} -gt 0 ]] || targets=("${all_targets[@]}")
if $check && $write; then
  echo "--check and --write are exclusive" >&2
  exit 2
fi
for t in "${targets[@]}"; do
  if [[ " ${all_targets[*]} " != *" $t "* ]]; then
    echo "unknown target '$t'; expected one of: ${all_targets[*]}" >&2
    exit 2
  fi
done
command -v jq > /dev/null || { echo "jq is required" >&2; exit 2; }
mkdir -p "$log_dir"

# The publish command of the phase 3.1 global constraints. Warnings-as-errors is overridden so every
# warning is printed and counted instead of failing the publish; TrimmerSingleWarn=false reports per
# method instead of one line per assembly. -tl:off keeps the classic console logger, whose one-line
# warnings the parser reads, whether or not a terminal is attached. Each publish gets a fresh --artifacts-path, so nothing is
# skipped as up to date: an incremental publish would print no warnings and count 0.
publish() {
  local target="$1" artifacts="$2"
  local common=(-c Release -r linux-x64 -p:TreatWarningsAsErrors=false -p:WarningsAsErrors=
    -p:TrimmerSingleWarn=false --artifacts-path "$artifacts" -tl:off)
  if [[ "$target" == Web ]]; then
    # Blazor WebAssembly: no PublishAot. RunAOTCompilation refuses to run without IL trimming, and the
    # project sets PublishTrimmed=false for its normal JIT publish, so the analysis turns it on here.
    dotnet publish "$repo/src/Daedalus.Web" "${common[@]}" \
      -p:RunAOTCompilation=true -p:PublishTrimmed=true -p:SuppressTrimAnalysisWarnings=false
  else
    dotnet publish "$repo/src/Daedalus.$target" "${common[@]}" -p:PublishAot=true
  fi
}

# normalise <log> <repo>: prints `root<TAB>key` for every IL2xxx/IL3xxx warning line, not yet deduplicated.
normalise() {
  awk -v repo="$2/" '
    function root_of(file, origin,    n, s, m, dir, out, i) {
      if (match(file, /(^|\/)src\/[^\/]+\//)) {
        dir = substr(file, RSTART, RLENGTH); sub(/^\/?src\//, "", dir); sub(/\/$/, "", dir)
        return dir
      }
      if (origin == "") return "(analyzer, " file ")"
      sub(/\(.*/, "", origin)
      while (gsub(/<[^<>]*>/, "", origin) > 0) {}
      n = split(origin, s, ".")
      m = 1
      if (s[1] == "Microsoft") m = (s[2] ~ /^(AspNetCore|Extensions|Agents)$/) ? 3 : 2
      if (s[1] == "System") m = (s[2] ~ /^(Text|Private|Runtime|Net|Security)$/) ? 3 : 2
      if (m > n) m = n
      out = s[1]
      for (i = 2; i <= m; i++) out = out "." s[i]
      return out
    }
    /warning IL[23][0-9][0-9][0-9]: / {
      line = $0
      sub(/\r$/, "", line)
      sub(/ \[[^][]*\]$/, "", line)
      match(line, /warning IL[23][0-9][0-9][0-9]: /)
      code = substr(line, RSTART + 8, 6)
      prefix = substr(line, 1, RSTART - 1)
      rest = substr(line, RSTART + RLENGTH)
      analysis = (prefix ~ /(Trim|AOT) analysis $/)
      sub(/(Trim|AOT) analysis $/, "", prefix)
      sub(/ *: *$/, "", prefix)
      sub(/^ +/, "", prefix)
      file = ""; ln = ""
      if (prefix != "ILC" && prefix != "ILLink" && prefix != "") {
        if (match(prefix, /\([0-9]+(,[0-9]+)*\)$/)) {
          file = substr(prefix, 1, RSTART - 1)
          ln = substr(prefix, RSTART + 1, RLENGTH - 2)
          sub(/,.*/, "", ln)
        } else {
          file = prefix
        }
        if (index(file, repo) == 1) file = substr(file, length(repo) + 1)
      }
      origin = ""; msg = rest
      if (analysis && (p = index(rest, ": ")) > 0) {
        origin = substr(rest, 1, p - 1)
        msg = substr(rest, p + 2)
      }
      key = (file != "") ? code "|" file "|" ln "|" msg : code "|||" rest
      print root_of(file, origin) "\t" key
    }
  ' "$1"
}

# error_lines <log>: prints the log's error lines (MSBuild, compiler, ILC, linker) and succeeds only when
# there is at least one. One awk, not a grep pipeline: with pipefail an early-exiting `grep -q` can
# turn a match into a SIGPIPE failure.
error_lines() {
  awk '!/warning [A-Z]+[0-9]+:/ && /(^|: )(fatal )?error( [A-Za-z]+[0-9]+)? ?:/ { found = 1; print }
       END { exit !found }' "$1"
}

declare -A counts
summaries=""
for t in "${targets[@]}"; do
  log="$log_dir/$t.log"
  artifacts="$(mktemp -d)"
  echo "publishing $t (log: $log)" >&2
  set +e
  publish "$t" "$artifacts" > "$log" 2>&1
  status=$?
  set -e
  rm -rf "$artifacts"
  if [[ $status -ne 0 ]] || error_lines "$log" > /dev/null; then
    echo "AOT publish of $t failed (exit $status); it is not counted. Errors from $log:" >&2
    error_lines "$log" | sort -u | head -20 >&2 || tail -20 "$log" >&2
    [[ -n "${GITHUB_ACTIONS:-}" ]] && echo "::error::AOT publish of $t failed (exit $status); see the step log."
    exit 2
  fi
  pairs="$(normalise "$log" "$repo" | sort -u)"
  if [[ -n "$pairs" ]]; then
    printf '%s\n' "$pairs" | cut -f2- | sort -u > "$log_dir/$t.keys"
  else
    : > "$log_dir/$t.keys"
  fi
  counts[$t]=$(wc -l < "$log_dir/$t.keys" | tr -d ' ')
  echo "$t ${counts[$t]}"
  if [[ -n "$pairs" ]]; then
    top="$(printf '%s\n' "$pairs" | cut -f1 | sort | uniq -c | sort -k1,1nr -k2 | head -5)"
    summaries+="Top root causes, $t:"$'\n'"$top"$'\n'
  fi
done
[[ -n "$summaries" ]] && printf '%s' "$summaries"

if $write; then
  current='{}'
  [[ -f "$baseline" ]] && current="$(cat "$baseline")"
  for t in "${targets[@]}"; do
    current="$(jq --arg t "$t" --argjson n "${counts[$t]}" '.[$t] = $n' <<< "$current")"
  done
  jq -S . <<< "$current" > "$baseline"
  echo "wrote $baseline" >&2
fi

if $check; then
  [[ -f "$baseline" ]] || { echo "no baseline at $baseline" >&2; exit 1; }
  failed=0
  for t in "${targets[@]}"; do
    expected="$(jq -r --arg t "$t" '.[$t] // empty' "$baseline")"
    actual="${counts[$t]}"
    if [[ -z "$expected" ]]; then
      msg="$t: no baseline entry, actual $actual. Add it with: eng/aot/count-warnings.sh --target $t --write"
    elif [[ "$actual" -gt "$expected" ]]; then
      msg="$t: baseline $expected, actual $actual. The count rose: fix the new warnings; compare $log_dir/$t.keys with a run on the base branch."
    elif [[ "$actual" -lt "$expected" ]]; then
      msg="$t: baseline $expected, actual $actual. The count fell: lower the baseline in this change with: eng/aot/count-warnings.sh --target $t --write"
    else
      continue
    fi
    echo "$msg" >&2
    [[ -n "${GITHUB_ACTIONS:-}" ]] && echo "::error::$msg"
    failed=1
  done
  [[ $failed -eq 0 ]] || exit 1
  echo "all counts match $baseline" >&2
fi
