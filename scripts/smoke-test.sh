#!/usr/bin/env bash
# Post-deploy smoke test. Read-only and credential-free: it proves the deployment is up, wired to its database, serving TLS with the
# expected security headers, and that development-only surfaces are NOT exposed.
#
#   scripts/smoke-test.sh https://api.staging.example https://portal.staging.example
#
# Environment: SMOKE_RETRIES (default 30) and SMOKE_DELAY seconds (default 5) while the stack warms up;
#              SMOKE_INSECURE=1 accepts a self-signed certificate (local rehearsals only); SMOKE_RESOLVE="host:443:ip,host2:443:ip" maps host names.
set -uo pipefail

api="${1:?usage: smoke-test.sh <api-base-url> <portal-base-url>}"
portal="${2:?usage: smoke-test.sh <api-base-url> <portal-base-url>}"
api="${api%/}"
portal="${portal%/}"
retries="${SMOKE_RETRIES:-30}"
delay="${SMOKE_DELAY:-5}"

curl_args=(--silent --show-error --max-time 20 --noproxy '*')
[ "${SMOKE_INSECURE:-0}" = "1" ] && curl_args+=(--insecure)
if [ -n "${SMOKE_RESOLVE:-}" ]; then
  IFS=',' read -ra resolves <<< "$SMOKE_RESOLVE"
  for entry in "${resolves[@]}"; do curl_args+=(--resolve "$entry"); done
fi

failures=0
pass() { printf 'ok    %s\n' "$1"; }
fail() { printf 'FAIL  %s\n' "$1"; failures=$((failures + 1)); }

status() {
  local out
  out="$(curl "${curl_args[@]}" -o /dev/null -w '%{http_code}' "$@" 2>/dev/null)" || true
  printf '%s' "${out:-000}"
}
headers() { curl "${curl_args[@]}" -D - -o /dev/null "$@" 2>/dev/null | tr -d '\r'; }

expect_status() { # description expected url [curl args...]
  local description="$1" expected="$2" url="$3"
  shift 3
  local actual
  actual="$(status "$@" "$url")"
  if [ "$actual" = "$expected" ]; then pass "$description ($actual)"; else fail "$description: expected $expected, got $actual"; fi
}

wait_for() { # url
  local i
  for ((i = 1; i <= retries; i++)); do
    [ "$(status "$1")" = "200" ] && return 0
    sleep "$delay"
  done
  return 1
}

echo "Smoke test: API $api, portal $portal"

if wait_for "$api/health/live"; then pass "API liveness"; else fail "API liveness never became 200"; fi
if wait_for "$api/health/ready"; then pass "API readiness (database + tenant protection)"; else fail "API readiness never became 200"; fi
if wait_for "$portal/health/live"; then pass "Portal liveness"; else fail "Portal liveness never became 200"; fi

api_headers="$(headers "$api/health/live")"
for expected in 'strict-transport-security:' 'x-content-type-options: nosniff' 'x-correlation-id:'; do
  if printf '%s\n' "$api_headers" | grep -qi "^$expected"; then pass "API header $expected"; else fail "API header missing: $expected"; fi
done
if printf '%s\n' "$api_headers" | grep -qi '^server: kestrel\|^x-powered-by'; then fail "API leaks Server/X-Powered-By"; else pass "API does not leak Kestrel/X-Powered-By"; fi

expect_status "API rejects anonymous /auth/me" 401 "$api/api/v1/auth/me"
expect_status "API wrong password is 401, not 500" 401 "$api/api/v1/auth/login" -X POST -H 'content-type: application/json' --data '{"email":"smoke@invalid.example","password":"not-a-real-password-1"}'
# Development-only surfaces must not answer 200 (an unauthenticated 401 or a 404 are both fine).
for path in /openapi/v1.json /swagger/index.html; do
  code="$(status "$api$path")"
  if [ "$code" = "200" ]; then fail "$path is exposed"; else pass "$path is not exposed ($code)"; fi
done

portal_headers="$(headers "$portal/login")"
if printf '%s\n' "$portal_headers" | grep -qi '^content-security-policy:.*nonce-'; then pass "Portal CSP with nonce"; else fail "Portal CSP nonce missing"; fi
expect_status "Portal login page" 200 "$portal/login"
expect_status "Portal protected area redirects anonymous users" 302 "$portal/"

case "$api" in
  https://*)
    plain="http://${api#https://}"
    code="$(status "$plain/api/v1/system/info")"
    if [ "$code" = "308" ] || [ "$code" = "301" ] || [ "$code" = "307" ] || [ "$code" = "000" ]; then pass "Plain HTTP is redirected or closed ($code)"; else fail "Plain HTTP answered $code (expected redirect or closed)"; fi
    ;;
esac

if [ "$failures" -gt 0 ]; then
  echo "SMOKE TEST FAILED: $failures check(s)"
  exit 1
fi
echo "Smoke test passed"
