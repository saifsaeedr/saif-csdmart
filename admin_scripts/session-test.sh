#!/bin/bash
# Drive a real browser login through cxb or catalog and poll an
# authenticated endpoint at fixed intervals. Use to diagnose reports
# of "session expires in N minutes" — surfaces both the JWT lifetime
# the server actually issued AND the exact error code the moment auth
# breaks, so you can tell whether the cause is JWT_ACCESS_EXPIRES,
# SESSION_INACTIVITY_TTL, clock skew, or a frontend bug.
#
# Usage:
#   ./admin_scripts/session-test.sh --url=URL --user=USER --pass=PASS \
#       [--duration=SECONDS] [--interval=SECONDS] [--ui=cxb|catalog]
#
# Defaults: url=http://localhost:8282/cxb/, user=dmart, duration=180,
# interval=30. UI is auto-detected from the URL (`/cat/` → catalog,
# everything else → cxb).
#
# Examples:
#   ./admin_scripts/session-test.sh --pass=Test1234
#   ./admin_scripts/session-test.sh --url=https://prod.example.com/cxb/ \
#       --user=admin --pass='****' --duration=900 --interval=60
#   ./admin_scripts/session-test.sh --url=https://prod.example.com/cat/ \
#       --user=admin --pass='****' --ui=catalog
#
# puppeteer-core installs into ~/.cache/dmart-smoke-test (shared with
# admin_scripts/smoke-test.sh) and uses the system google-chrome —
# no browser download.

set -eu

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORKDIR="${SMOKE_WORKDIR:-$HOME/.cache/dmart-smoke-test}"

if ! command -v "${SMOKE_CHROME_PATH:-google-chrome}" > /dev/null 2>&1; then
    echo "google-chrome not found in PATH (set SMOKE_CHROME_PATH=/path/to/chrome to override)" >&2
    exit 2
fi

mkdir -p "$WORKDIR"
if ! [ -d "$WORKDIR/node_modules/puppeteer-core" ]; then
    echo "Installing puppeteer-core into $WORKDIR (one-time, ~10 MB)..."
    (cd "$WORKDIR" && npm init -y > /dev/null && npm install --no-audit --no-fund puppeteer-core@latest > /dev/null)
fi

cp "$REPO_ROOT/admin_scripts/session-test.mjs" "$WORKDIR/session-test.mjs"
exec node "$WORKDIR/session-test.mjs" "$@"
