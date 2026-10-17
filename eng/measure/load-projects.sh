#!/usr/bin/env bash
# Sends 200 authenticated GET /api/projects at concurrency 10 and exits non-zero unless every reply is 2xx.
# Usage: load-projects.sh <api-base-url>
#
# The API's global rate limit is 100 requests a minute per signed-in subject (Daedalus.Api/Program.cs), so 200
# requests as one user get 100 replies of 429 that never reach the database. The realm has two users that may
# read projects, dev and admin, so the requests alternate between their tokens: 100 each, exactly the permit limit.
#
# The client secret and both passwords are read from keycloak-realm.json into variables. They are never echoed,
# never written to a file and never passed on a command line: each token request body and each Authorization header
# reaches curl through stdin.
set -euo pipefail
MEASURE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$MEASURE_DIR/../.." && pwd)"
BASE="${1:?usage: load-projects.sh <api-base-url>}"
REQUESTS=200
CONCURRENCY=10
KC_PORT="${MEASURE_KC_PORT:-8082}"
TOKEN_URL="http://localhost:${KC_PORT}/realms/daedalus/protocol/openid-connect/token"
REALM="$REPO_ROOT/keycloak-realm.json"

client_secret="$(jq -r '.clients[] | select(.clientId=="daedalus-api") | .secret | @uri' "$REALM")"
[ -n "$client_secret" ] || { echo "load-projects: client secret missing from the realm" >&2; exit 2; }

token_for() { # <username>
  local password token
  password="$(jq -r --arg u "$1" '.users[] | select(.username==$u) | .credentials[0].value | @uri' "$REALM")"
  [ -n "$password" ] || { echo "load-projects: no password for $1 in the realm" >&2; exit 2; }
  token="$(printf 'grant_type=password&client_id=daedalus-api&client_secret=%s&username=%s&password=%s&scope=openid' \
      "$client_secret" "$1" "$password" \
    | curl -s --max-time 10 -d @- "$TOKEN_URL" | jq -r '.access_token // empty')"
  [ -n "$token" ] || { echo "load-projects: no access token for $1" >&2; exit 2; }
  printf '%s' "$token"
}
TOKEN_A="$(token_for dev)"
TOKEN_B="$(token_for admin)"
unset client_secret
export TOKEN_A TOKEN_B BASE

# The script passed to bash -c is single-quoted on purpose: its variables expand in the child shell.
# shellcheck disable=SC2016
codes="$(seq "$REQUESTS" | xargs -P "$CONCURRENCY" -I{} bash -c '
  if [ $(( {} % 2 )) -eq 0 ]; then t="$TOKEN_A"; else t="$TOKEN_B"; fi
  printf "header = \"Authorization: Bearer %s\"\n" "$t" \
    | curl -s -o /dev/null -w "%{http_code}\n" --max-time 30 -K - "$BASE/api/projects"')"
unset TOKEN_A TOKEN_B
bad="$(grep -vc '^2' <<<"$codes" || true)"
total="$(grep -c . <<<"$codes")"
if [ "$total" != "$REQUESTS" ] || [ "$bad" != "0" ]; then
  echo "load-projects: $bad of $total replies were not 2xx; status counts: $(sort <<<"$codes" | uniq -c | tr '\n' ' ')" >&2
  exit 1
fi
echo "load-projects: $total replies, all 2xx" >&2
