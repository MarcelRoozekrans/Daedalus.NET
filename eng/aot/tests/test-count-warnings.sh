#!/usr/bin/env bash
# Self-test for eng/aot/count-warnings.sh. It needs no SDK and no network: a fake `dotnet` first on
# PATH prints a canned publish log and exits with a chosen code, so the parser, the dedup key, the
# failure handling and the ratchet are tested on fixed input.
# Run: eng/aot/tests/test-count-warnings.sh
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
counter="$here/../count-warnings.sh"
repo="$(cd "$here/../../.." && pwd)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/logs"

# The sample's paths are written as @REPO@, so the repo-relative normalisation is tested on any machine.
sample="$work/sample-ilc.log"
# Parameter expansion, not sed: a repo path holding & or # would corrupt a sed replacement. The
# replacement is quoted, because bash 5.2 otherwise expands & in it to the matched text.
while IFS= read -r line || [[ -n "$line" ]]; do
  printf '%s\n' "${line//@REPO@/"$repo"}"
done < "$here/sample-ilc.log" > "$sample"

# The fake SDK: prints $FAKE_LOG and exits with $FAKE_EXIT, whatever the arguments.
cat > "$work/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
cat "$FAKE_LOG"
exit "${FAKE_EXIT:-0}"
EOF
chmod +x "$work/bin/dotnet"

failures=0
pass() { printf 'ok   - %s\n' "$1"; }
fail() { printf 'FAIL - %s\n' "$1"; failures=$((failures + 1)); }

# run_counter <log> <exit> <args...>: runs the counter against the fake SDK; sets $out and $rc.
run_counter() {
  local log="$1" code="$2"
  shift 2
  set +e
  out="$(PATH="$work/bin:$PATH" FAKE_LOG="$log" FAKE_EXIT="$code" \
    AOT_BASELINE="$work/baseline.json" AOT_LOG_DIR="$work/logs" \
    "$counter" "$@" 2>&1)"
  rc=$?
  set -e
}

echo '{ "Api": 100, "Sandbox": 3 }' > "$work/baseline.json"

# 1. The sample holds 3 distinct IL warnings, an exact repeat (MSBuild's end-of-build summary), the
#    Roslyn-analyzer twin of an ILC warning at the same file and line, and a non-IL warning.
#    Red: count every IL line -> 5.
#    Red: dedup whole lines with sort -u, without normalising -> 4.
run_counter "$sample" 0 --target Sandbox
if [[ $rc -eq 0 && "$out" == *"Sandbox 3"* ]]; then pass "sample log counts 3 distinct warnings"
else fail "sample log counts 3 distinct warnings (rc=$rc): $out"; fi

# 2. The dedup key is code|file|line|member, with the file made repo-relative and the column dropped.
#    MapGet and MapPost share an origin member and have no location, so the member field must carry
#    the member each one uses, or two distinct warnings collapse into one.
#    Red: key on the origin member alone -> the two Map keys become one line.
expected_keys="$work/expected.keys"
cat > "$expected_keys" <<'EOF'
IL2026|||Thalos.Sandbox.Host.SandboxHost.Map(WebApplication): Using member 'Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(IEndpointRouteBuilder,String,Delegate)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. This API may perform reflection on the supplied delegate and its parameters. These types may be trimmed if not directly referenced.
IL2026|||Thalos.Sandbox.Host.SandboxHost.Map(WebApplication): Using member 'Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapPost(IEndpointRouteBuilder,String,Delegate)' which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. This API may perform reflection on the supplied delegate and its parameters. These types may be trimmed if not directly referenced.
IL3050|src/Daedalus.Sandbox/Program.cs|5|Using member 'Thalos.Sandbox.Host.SandboxHost.CreateBuilder(String[])' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. Tool parameters and results are serialized via reflection-based JSON.
EOF
if [[ -f "$work/logs/Sandbox.keys" ]] && diff -u "$expected_keys" "$work/logs/Sandbox.keys" > "$work/keys.diff"; then
  pass "normalised keys are code|file|line|member"
else fail "normalised keys are code|file|line|member: $(cat "$work/keys.diff" 2>/dev/null)"; fi

# 3. The top root causes follow the counts: Thalos (2 unlocated warnings), then the project the
#    located warning sits in.
#    Red: group every warning under one bucket -> no "2 Thalos" line.
if [[ "$out" == *"2 Thalos"* && "$out" == *"1 "*"Daedalus.Sandbox"* ]]; then pass "root causes are grouped"
else fail "root causes are grouped: $out"; fi

# 4. A publish whose log ends in a link error, with a non-zero exit, fails with exit 2 naming the target.
#    The match is the failure message itself: `publishing Sandbox` is always in the output.
#    Red: treat an error as a pass -> exit 0.
#    Red: drop the target from the failure message -> no "AOT publish of Sandbox failed".
cp "$sample" "$work/link-error.log"
cat >> "$work/link-error.log" <<EOF
/home/runner/.nuget/packages/microsoft.dotnet.ilcompiler/10.0.0/build/Microsoft.NETCore.Native.Publish.targets(392,5): error MSB3073: The command ""clang" "@obj/native/Daedalus.Sandbox.ilc.rsp"" exited with code 1. [$repo/src/Daedalus.Sandbox/Daedalus.Sandbox.csproj]
EOF
run_counter "$work/link-error.log" 1 --target Sandbox
if [[ $rc -eq 2 && "$out" == *"AOT publish of Sandbox failed"* ]]; then pass "link error exits 2 naming the target"
else fail "link error exits 2 naming the target (rc=$rc): $out"; fi

# 5. An error line fails the target even when the publish exit code is 0.
#    Red: look only at the exit code -> exit 0.
run_counter "$work/link-error.log" 0 --target Sandbox
if [[ $rc -eq 2 ]]; then pass "an error line exits 2 despite exit code 0"
else fail "an error line exits 2 despite exit code 0 (rc=$rc): $out"; fi

# 6. A non-zero publish exit fails the target even with no error line in the log.
#    Red: look only for error lines -> exit 0.
run_counter "$sample" 1 --target Sandbox
if [[ $rc -eq 2 ]]; then pass "a non-zero publish exit exits 2 without an error line"
else fail "a non-zero publish exit exits 2 without an error line (rc=$rc): $out"; fi

# 7. --check passes when the count equals the baseline.
#    Red: fail every --check -> exit 1.
run_counter "$sample" 0 --target Sandbox --check
if [[ $rc -eq 0 ]]; then pass "--check passes on an equal count"
else fail "--check passes on an equal count (rc=$rc): $out"; fi

# 8. --check fails when the count is above the baseline, naming target, baseline and actual count.
#    Red: fail only when the count fell -> exit 0.
#    Red: drop the target from the message -> no "Sandbox: baseline 2, actual 3".
echo '{ "Api": 100, "Sandbox": 2 }' > "$work/baseline.json"
run_counter "$sample" 0 --target Sandbox --check
if [[ $rc -eq 1 && "$out" == *"Sandbox: baseline 2, actual 3. The count rose"* ]]; then pass "--check fails on a rise"
else fail "--check fails on a rise (rc=$rc): $out"; fi

# 9. --check fails when the count is below the baseline: the baseline must be lowered in the same change.
#    Red: fail only when the count rose -> exit 0.
#    Red: drop the target from the message -> no "Sandbox: baseline 4, actual 3".
echo '{ "Api": 100, "Sandbox": 4 }' > "$work/baseline.json"
run_counter "$sample" 0 --target Sandbox --check
if [[ $rc -eq 1 && "$out" == *"Sandbox: baseline 4, actual 3. The count fell"* ]]; then pass "--check fails on a fall"
else fail "--check fails on a fall (rc=$rc): $out"; fi

# 10. --write sets the counted target and keeps the others.
#     Red: write only the counted targets -> Api disappears.
run_counter "$sample" 0 --target Sandbox --write
if [[ $rc -eq 0 && "$(jq -c . "$work/baseline.json")" == '{"Api":100,"Sandbox":3}' ]]; then pass "--write updates one target"
else fail "--write updates one target (rc=$rc): $(cat "$work/baseline.json") $out"; fi

# 11. An unknown target is refused.
#     Red: accept any name -> the fake publish runs and exits 0.
run_counter "$sample" 0 --target Nope
if [[ $rc -ne 0 && "$out" == *"Nope"* ]]; then pass "unknown target is refused"
else fail "unknown target is refused (rc=$rc): $out"; fi

if [[ $failures -gt 0 ]]; then
  echo "$failures check(s) failed"
  exit 1
fi
echo "all checks passed"
