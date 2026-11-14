#!/usr/bin/env bash
# Drives one manufacture run on a running Daedalus Api, issue #452. The Live native run workflow calls it once the
# native Api answers /health; see README.md beside it for what each mode proves.
# Usage: drive.sh <api-base-url>
#
#   DRY_RUN=true    creates the project and the task, checks that Manufacture refuses a repository that is not
#                   allow-listed, and stops before starting a run: no model call, no sandbox, no pull request.
#   DRY_RUN=false   also presses Manufacture on the task, approves the run at its gate, polls it to a terminal
#                   status and reads the cost. Exits non-zero unless the run succeeded and opened a pull request.
#
# Optional: REPOSITORY_URL, the project's repository, which must match Thalos:Workflow:Repositories;
# TASK_TITLE and TASK_DESCRIPTION; RUN_TIMEOUT_MINUTES, 120 by default; POLL_SECONDS, 30 by default;
# KC_PORT, 8082 by default; OUT_DIR for run.json, cost.json and summary.md, a temporary directory by default.
# The summary is appended to GITHUB_STEP_SUMMARY when that is set.
#
# Credentials: the realm's client secret and the dev user's password are read from keycloak-realm.json into
# variables, as eng/measure/load-projects.sh does. They and the access token are never echoed, never written to a
# file and never passed on a command line: the token request body and the Authorization header reach curl on stdin.
set -euo pipefail
export LC_ALL=C

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$DIR/../.." && pwd)"
BASE="${1:?usage: drive.sh <api-base-url>}"
BASE="${BASE%/}"
DRY_RUN="${DRY_RUN:-true}"
REPOSITORY_URL="${REPOSITORY_URL:-https://github.com/MarcelRoozekrans/daedalus-sandbox}"
TASK_TITLE="${TASK_TITLE:-Add Fahrenheit and kelvin conversions to TemperatureConverter}"
TASK_DESCRIPTION="${TASK_DESCRIPTION:-Add FahrenheitToKelvin and KelvinToFahrenheit to src/Sandbox/TemperatureConverter.cs, \
rejecting input below absolute zero the way the existing methods do, with tests beside the existing ones.}"
RUN_TIMEOUT_MINUTES="${RUN_TIMEOUT_MINUTES:-120}"
POLL_SECONDS="${POLL_SECONDS:-30}"
KC_PORT="${KC_PORT:-8082}"
TOKEN_URL="http://localhost:${KC_PORT}/realms/daedalus/protocol/openid-connect/token"
REALM="$REPO_ROOT/keycloak-realm.json"
USERNAME=dev   # project-manager, task-manager and developer: may create both, press Manufacture and resume a run
OUT_DIR="${OUT_DIR:-$(mktemp -d)}"
mkdir -p "$OUT_DIR"
RESP="$(mktemp)"
trap 'rm -f "$RESP"' EXIT

case "$DRY_RUN" in true|false) ;; *) echo "drive: DRY_RUN must be true or false, not '$DRY_RUN'" >&2; exit 2 ;; esac
[[ "$RUN_TIMEOUT_MINUTES" =~ ^[1-9][0-9]*$ ]] || { echo "drive: RUN_TIMEOUT_MINUTES must be a positive integer" >&2; exit 2; }
[[ "$POLL_SECONDS" =~ ^[1-9][0-9]*$ ]] || { echo "drive: POLL_SECONDS must be a positive integer" >&2; exit 2; }
for tool in curl jq; do command -v "$tool" >/dev/null || { echo "drive: $tool is required" >&2; exit 2; }; done

log() { echo "[drive] $*" >&2; }
die() { echo "::error::drive: $*" >&2; exit 1; }

# --- the access token ------------------------------------------------------------------------------------------
# The realm's access tokens live 300 s and a run takes far longer, so the token is renewed once it is 240 s old.
# It lives only in this shell: call refresh_token in the main shell before every api call, never inside $(...).
TOKEN=""
TOKEN_AT=0
refresh_token() {
  local now secret password
  now="$(date +%s)"
  if [ -n "$TOKEN" ] && [ $(( now - TOKEN_AT )) -lt 240 ]; then return 0; fi
  secret="$(jq -r '.clients[] | select(.clientId=="daedalus-api") | .secret | @uri' "$REALM")"
  password="$(jq -r --arg u "$USERNAME" '.users[] | select(.username==$u) | .credentials[0].value | @uri' "$REALM")"
  [ -n "$secret" ] && [ -n "$password" ] || die "the client secret or the $USERNAME password is missing from the realm"
  TOKEN="$(printf 'grant_type=password&client_id=daedalus-api&client_secret=%s&username=%s&password=%s&scope=openid' \
      "$secret" "$USERNAME" "$password" \
    | curl -s --max-time 15 -d @- "$TOKEN_URL" | jq -r '.access_token // empty')"
  [ -n "$TOKEN" ] || die "Keycloak gave no access token for $USERNAME"
  TOKEN_AT="$now"
}

# api <method> <path> [json-body]: prints the HTTP status, 000 on a transport failure, and leaves the body in $RESP.
api() {
  local args=(-s -o "$RESP" -w '%{http_code}' --max-time 60 -X "$1" -H 'Accept: application/json')
  local code
  if [ -n "${3:-}" ]; then args+=(-H 'Content-Type: application/json' --data-binary "$3"); fi
  : >"$RESP"
  code="$(printf 'header = "Authorization: Bearer %s"\n' "$TOKEN" | curl "${args[@]}" -K - "$BASE$2")" || true
  echo "${code:-000}"
}

# expect <status> <what>: fails with the body when the last call answered anything else.
expect() {
  local got="$1" want="$2" what="$3"
  [ "$got" = "$want" ] || die "$what answered $got, expected $want: $(head -c 2000 "$RESP")"
}

create_project() { # <name> <repository-url>; prints the project id
  local body status
  body="$(jq -cn --arg n "$1" --arg u "$2" \
    '{projectName:$n, description:"Created by the Live native run workflow, issue #452.", version:"1.0",
      repositoryUrl:$u, defaultBranch:"main"}')"
  refresh_token
  status="$(api POST /api/projects "$body")"
  expect "$status" 201 "POST /api/projects"
  jq -er '.id' "$RESP"
}

create_task() { # <project-id> <task-id> <title> <description>; prints the task's Guid
  local body status
  body="$(jq -cn --arg p "$1" --arg t "$2" --arg title "$3" --arg d "$4" \
    '{projectId:$p, taskId:$t, title:$title, description:$d, priority:1, phase:null, parallelGroup:null,
      estimatedComplexity:0, prompt:null, dependencies:[], filesToModify:[]}')"
  refresh_token
  status="$(api POST /api/tasks "$body")"
  expect "$status" 201 "POST /api/tasks"
  jq -er '.id' "$RESP"
}

label="$(date -u +%Y%m%dT%H%M%SZ)"
mode="$([ "$DRY_RUN" = true ] && echo dry || echo live)"
log "mode: $mode, Api: $BASE, repository: $REPOSITORY_URL"

project_id="$(create_project "live-native-run $label" "$REPOSITORY_URL")"
log "project $project_id"
task_id="$(create_task "$project_id" LIVE-1 "$TASK_TITLE" "$TASK_DESCRIPTION")"
log "task $task_id"

refresh_token
status="$(api GET "/api/tasks/$task_id")"
expect "$status" 200 "GET /api/tasks/$task_id"
[ "$(jq -r '.projectId' "$RESP")" = "$project_id" ] || die "task $task_id does not belong to project $project_id"
[ "$(jq -r '.workflowRunId' "$RESP")" = null ] || die "the new task already carries a run"

# Manufacture on a project whose repository is not allow-listed: refused with 422 before any run starts, so it
# costs nothing. It proves the route, the WorkflowResume policy and the allow-list match on this host.
other_project="$(create_project "live-native-run $label refused" "https://github.com/example/not-allow-listed")"
other_task="$(create_task "$other_project" LIVE-0 "Refused on purpose" "Its repository is not allow-listed.")"
refresh_token
status="$(api POST "/api/tasks/$other_task/manufacture")"
expect "$status" 422 "Manufacture on a repository that is not allow-listed"
grep -q 'matches no allow-listed entry' "$RESP" || die "the 422 does not name the allow-list: $(head -c 2000 "$RESP")"
log "Manufacture refused the repository that is not allow-listed, as it should"

summary() { # appends stdin to the job summary and to $OUT_DIR/summary.md
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then tee -a "$OUT_DIR/summary.md" >>"$GITHUB_STEP_SUMMARY"; else tee -a "$OUT_DIR/summary.md"; fi
}

if [ "$DRY_RUN" = true ]; then
  summary <<EOF
## Live native run: dry mode

The native Api, in sandbox mode, created project \`$project_id\` and task \`$task_id\` on \`$REPOSITORY_URL\`,
and Manufacture refused a repository that is not allow-listed with 422. Stopped before Manufacture: no model call,
no sandbox run, no pull request.
EOF
  log "dry mode: stopped before Manufacture"
  exit 0
fi

# --- the live run ----------------------------------------------------------------------------------------------
refresh_token
status="$(api POST "/api/tasks/$task_id/manufacture")"
expect "$status" 201 "POST /api/tasks/$task_id/manufacture"
run_id="$(jq -er '.runId' "$RESP")"
log "run $run_id started"

deadline=$(( $(date +%s) + RUN_TIMEOUT_MINUTES * 60 ))
approved=false
last=""
run_status=""
while :; do
  refresh_token
  status="$(api GET "/api/workflow-runs/$run_id")"
  if [ "$status" != 200 ]; then
    # One failed read is not the run's outcome; the deadline bounds the retries.
    log "GET /api/workflow-runs/$run_id answered $status; retrying"
  else
    cp "$RESP" "$OUT_DIR/run.json"
    run_status="$(jq -r '.status' "$RESP")"
    node="$(jq -r '.currentNode' "$RESP")"
    signal="$(jq -r '.awaitingSignal // ""' "$RESP")"
    if [ "$run_status/$node/$signal" != "$last" ]; then log "status $run_status, node $node${signal:+, awaiting $signal}"; last="$run_status/$node/$signal"; fi
    case "$run_status" in
      Succeeded|Failed|Cancelled) break ;;
      Awaiting)
        # The gate before publish: the approval a person gives on the run page, without the standing-instructions
        # proposal, so the pull request carries the change only.
        [ "$signal" = human_approval ] || die "run $run_id is parked on '$signal', which this script does not answer"
        if [ "$approved" = false ]; then
          status="$(api POST "/api/workflow-runs/$run_id/resume" '{"signal":"human_approval","payload":null}')"
          expect "$status" 204 "POST /api/workflow-runs/$run_id/resume"
          approved=true
          log "approved the run at its gate"
        fi
        ;;
    esac
  fi
  [ "$(date +%s)" -lt "$deadline" ] || die "run $run_id did not finish within $RUN_TIMEOUT_MINUTES minutes; last status ${run_status:-unknown}"
  sleep "$POLL_SECONDS"
done

refresh_token
status="$(api GET /api/cost-analytics/summary)"
expect "$status" 200 "GET /api/cost-analytics/summary"
cp "$RESP" "$OUT_DIR/cost.json"
total_cost="$(jq -r '.totalCost' "$RESP")"
manufacture_cost="$(jq -r '[.bySource[] | select(.key=="manufacture") | .cost] | add // 0' "$RESP")"
tokens="$(jq -r '"\(.totalInputTokens) in, \(.totalOutputTokens) out, \(.totalCacheReadTokens) cache read, \(.totalCacheWriteTokens) cache write"' "$RESP")"
pr_url="$(jq -r '.prUrl // ""' "$OUT_DIR/run.json")"
reason="$(jq -r '.failureReason // .lastError // ""' "$OUT_DIR/run.json")"

summary <<EOF
## Live native run

| | |
|---|---|
| Run | \`$run_id\` |
| Status | $run_status |
| Pull request | ${pr_url:-none} |
| Cost, manufacture | \$$manufacture_cost |
| Cost, total | \$$total_cost |
| Tokens | $tokens |
${reason:+| Failure | $reason |}
EOF

[ "$run_status" = Succeeded ] || die "run $run_id ended $run_status${reason:+: $reason}"
[ -n "$pr_url" ] || die "run $run_id succeeded but recorded no pull request"
log "run $run_id succeeded: $pr_url, \$$manufacture_cost"
