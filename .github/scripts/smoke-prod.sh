#!/usr/bin/env bash
# Post-deploy smoke check for the sign-in plumbing and the cross-site guard.
#
# It cannot sign anyone in (that needs a real account), but it proves every
# piece that broke or could break silently: each configured provider's login
# redirects to the right authorize endpoint with PKCE and a state, the provider's
# POST back to /auth/callback/* reaches the sign-in handler instead of being
# refused by the CSRF guard (the 2026-10-23 outage), failed sign-ins land on the
# frontend, writes from foreign origins are refused, CORS only admits the
# frontend, and the frontend is serving and pointed at this API.
#
# Usage:  .github/scripts/smoke-prod.sh
#         API=https://api.example.com SITE=https://app.example.com .github/scripts/smoke-prod.sh
# Needs only bash and curl. Exit status is the number of failed checks (capped at 1).
set -u

API="${API:-https://api.rss.brandonchastain.com}"
SITE="${SITE:-https://rss.brandonchastain.com}"
EVIL="https://evil.example"
CURL=(curl -s --max-time 60)

failures=0
pass() { echo "  ok    $1"; }
fail() { echo "  FAIL  $1"; failures=$((failures + 1)); }
check() { # check "description" <condition exit code>
  if [ "$2" -eq 0 ]; then pass "$1"; else fail "$1"; fi
}
header() { # header <name> <headers-file>  -> last value of that header, trimmed
  grep -i "^$1:" "$2" | tail -n 1 | sed -E "s/^[^:]+:[[:space:]]*//; s/\r$//"
}
status() { head -n 1 "$1" | awk '{print $2}'; }

tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

echo "Smoke check: API=$API SITE=$SITE"

echo "Backend"
# A deploy's startup gate answers healthz 200 with "phase":"seeding" and 503 elsewhere
# until the new revision has a database. Wait (up to 3 minutes) for the real host.
for _ in $(seq 1 36); do
  "${CURL[@]}" -D "$tmp/h" -o "$tmp/b" "$API/api/healthz"
  if [ "$(status "$tmp/h")" = "200" ] && ! grep -q '"phase":"seeding"' "$tmp/b"; then break; fi
  echo "  ...waiting for the real host (the startup gate is still up)"
  sleep 5
done
check "healthz answers 200" "$([ "$(status "$tmp/h")" = "200" ]; echo $?)"
check "healthz reports healthy" "$(grep -q '"status":"healthy"' "$tmp/b"; echo $?)"

echo "Providers and login redirects"
"${CURL[@]}" -o "$tmp/providers" "$API/auth/providers"
providers="$(tr -d '[]" \r\n' < "$tmp/providers" | tr ',' ' ')"
check "at least one login provider is configured ($providers)" "$([ -n "$providers" ]; echo $?)"

for p in $providers; do
  case "$p" in
    microsoft) want_prefix="https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize"; want_pkce=1 ;;
    google)    want_prefix="https://accounts.google.com/"; want_pkce=1 ;;
    github)    want_prefix="https://github.com/login/oauth/authorize"; want_pkce=0 ;;
    *)         want_prefix=""; want_pkce=0 ;;
  esac
  "${CURL[@]}" -D "$tmp/h" -o /dev/null "$API/auth/login/$p?returnUrl=$SITE/"
  loc="$(header location "$tmp/h")"
  check "$p: login answers 302" "$([ "$(status "$tmp/h")" = "302" ]; echo $?)"
  check "$p: redirects to the provider's authorize endpoint" "$([ -n "$want_prefix" ] && [[ "$loc" == "$want_prefix"* ]]; echo $?)"
  check "$p: carries a client_id" "$([[ "$loc" == *"client_id="?* ]]; echo $?)"
  check "$p: carries a state" "$([[ "$loc" == *"state="?* ]]; echo $?)"
  check "$p: redirect_uri is this API's callback" "$([[ "$loc" == *"redirect_uri=$(printf '%s' "$API/auth/callback/$p" | sed 's|:|%3A|; s|/|%2F|g')"* ]]; echo $?)"
  if [ "$want_pkce" = "1" ]; then
    check "$p: uses PKCE (S256)" "$([[ "$loc" == *"code_challenge="?* && "$loc" == *"code_challenge_method=S256"* ]]; echo $?)"
  fi
  check "$p: sets a correlation cookie for the callback" "$(grep -qi '^set-cookie: .*correlation' "$tmp/h"; echo $?)"

  # The provider POSTs the authorization response back from its own origin.
  case "$p" in
    microsoft) provider_origin="https://login.microsoftonline.com" ;;
    google)    provider_origin="https://accounts.google.com" ;;
    *)         provider_origin="https://github.com" ;;
  esac
  "${CURL[@]}" -D "$tmp/h" -o "$tmp/b" -X POST -H "Origin: $provider_origin" -d "code=smoke&state=smoke" "$API/auth/callback/$p"
  check "$p: provider callback is not refused by the cross-site guard" "$(! grep -q 'Cross-site' "$tmp/b"; echo $?)"
  check "$p: provider callback reaches the handler (redirects)" "$([ "$(status "$tmp/h")" = "302" ]; echo $?)"
  check "$p: a failed sign-in returns to the frontend, not the API host" "$([[ "$(header location "$tmp/h")" == "$SITE/"* && "$(header location "$tmp/h")" == *"authError="* ]]; echo $?)"
done

echo "Cross-site guard and CORS"
"${CURL[@]}" -D "$tmp/h" -o "$tmp/b" -X POST -H "Origin: $EVIL" "$API/api/item/markAsRead?itemId=1&isRead=true"
check "write from a foreign origin is refused with 403" "$([ "$(status "$tmp/h")" = "403" ] && grep -q 'Cross-site' "$tmp/b"; echo $?)"
"${CURL[@]}" -D "$tmp/h" -o /dev/null -X POST -H "Origin: null" "$API/api/item/markAsRead?itemId=1&isRead=true"
check "write from a null origin is refused with 403" "$([ "$(status "$tmp/h")" = "403" ]; echo $?)"
"${CURL[@]}" -D "$tmp/h" -o /dev/null -X POST -H "Origin: $SITE" "$API/api/item/markAsRead?itemId=1&isRead=true"
check "write from the frontend origin passes the guard (401 without a session)" "$([ "$(status "$tmp/h")" = "401" ]; echo $?)"
"${CURL[@]}" -D "$tmp/h" -o /dev/null "$API/api/item/markAsRead?itemId=1&isRead=true"
check "mark-as-read has no GET route" "$([ "$(status "$tmp/h")" = "405" ]; echo $?)"
"${CURL[@]}" -D "$tmp/h" -o /dev/null -X OPTIONS -H "Origin: $SITE" -H "Access-Control-Request-Method: POST" -H "Access-Control-Request-Headers: content-type" "$API/api/feed"
check "CORS admits the frontend origin with credentials" "$([ "$(header access-control-allow-origin "$tmp/h")" = "$SITE" ] && [ "$(header access-control-allow-credentials "$tmp/h")" = "true" ]; echo $?)"
"${CURL[@]}" -D "$tmp/h" -o /dev/null -X OPTIONS -H "Origin: $EVIL" -H "Access-Control-Request-Method: POST" "$API/api/feed"
check "CORS does not admit a foreign origin" "$([ -z "$(header access-control-allow-origin "$tmp/h")" ]; echo $?)"
"${CURL[@]}" -o "$tmp/b" "$API/auth/me"
check "an anonymous session has no principal" "$(grep -q '"clientPrincipal":null' "$tmp/b"; echo $?)"

echo "Retired gateway headers"
"${CURL[@]}" -D "$tmp/h" -o /dev/null -H "X-Gateway-Key: smoke" -H "X-User-Id: e30=" "$API/api/user"
check "the API ignores the old proxy's headers (401)" "$([ "$(status "$tmp/h")" = "401" ]; echo $?)"

echo "Frontend"
"${CURL[@]}" -D "$tmp/h" -o "$tmp/b" "$SITE/"
check "site answers 200" "$([ "$(status "$tmp/h")" = "200" ]; echo $?)"
"${CURL[@]}" -D "$tmp/h" -o "$tmp/b" "$SITE/appsettings.json"
check "frontend is pointed at this API" "$(grep -q "\"ApiBaseUrl\": *\"$API/\"" "$tmp/b"; echo $?)"
check "frontend config carries no Static Web Apps login switch" "$(grep -q 'UseStaticWebApps' "$tmp/b"; [ $? -ne 0 ]; echo $?)"
"${CURL[@]}" -D "$tmp/h" -o /dev/null "$SITE/js/admin-chart.js"
check "frontend scripts are served" "$([ "$(status "$tmp/h")" = "200" ]; echo $?)"

echo
if [ "$failures" -gt 0 ]; then
  echo "Smoke check FAILED: $failures check(s) failed. Sign-in or the cross-site guard may be broken; see the FAIL lines above."
  exit 1
fi
echo "Smoke check passed."
