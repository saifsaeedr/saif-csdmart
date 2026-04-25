#!/bin/bash
# Browser smoke test for cxb + catalog dist bundles.
#
# Spins up a static HTTP server, mounts both UI dist trees under
# /cxb/ and /cat/ (matching the <base href="..."> the SPAs were built
# with), and runs admin_scripts/smoke-test.mjs through puppeteer-core
# against the local URLs. The smoke script exits non-zero if Chrome
# logged any uncaught exception, console.error, failed JS request, or
# dynamic-import failure across the chunks in either dist tree.
#
# puppeteer-core installs into ~/.cache/dmart-smoke-test (cached
# across runs) and uses the system google-chrome binary — no browser
# download needed.
#
# Usage: ./admin_scripts/smoke-test.sh
# Prerequisite: ./build-ui.sh has produced cxb/dist/client and catalog/dist/client.
# Optional env: SMOKE_CHROME_PATH=/path/to/chrome (default /usr/bin/google-chrome).

set -eu

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

CXB_DIST="$REPO_ROOT/cxb/dist/client"
CAT_DIST="$REPO_ROOT/catalog/dist/client"
WORKDIR="${SMOKE_WORKDIR:-$HOME/.cache/dmart-smoke-test}"
PORT="${SMOKE_PORT:-8123}"

if ! [ -d "$CXB_DIST" ] || ! [ -d "$CAT_DIST" ]; then
    echo "build artifacts missing — run ./build-ui.sh first" >&2
    exit 2
fi

if ! command -v "${SMOKE_CHROME_PATH:-google-chrome}" > /dev/null 2>&1; then
    echo "google-chrome not found in PATH (set SMOKE_CHROME_PATH=/path/to/chrome to override)" >&2
    exit 2
fi

# Bootstrap puppeteer-core in a stable workdir, only if the cache is
# missing or stale.
mkdir -p "$WORKDIR"
if ! [ -d "$WORKDIR/node_modules/puppeteer-core" ]; then
    echo "Installing puppeteer-core into $WORKDIR (one-time, ~10 MB)..."
    (cd "$WORKDIR" && npm init -y > /dev/null && npm install --no-audit --no-fund puppeteer-core@latest > /dev/null)
fi

# Mount cxb and catalog under /cxb and /cat so the <base href="..."> in
# the SPA index.html files resolves correctly.
ROOT="$(mktemp -d)"
trap 'rm -rf "$ROOT"; [ -n "${SERVER_PID:-}" ] && kill "$SERVER_PID" 2>/dev/null || true' EXIT
ln -s "$CXB_DIST" "$ROOT/cxb"
ln -s "$CAT_DIST" "$ROOT/cat"

# Pick http server. python3 is the cheapest universal option.
if ! command -v python3 > /dev/null 2>&1; then
    echo "python3 not found — needed for the static HTTP server" >&2
    exit 2
fi
python3 -m http.server "$PORT" --directory "$ROOT" > /dev/null 2>&1 &
SERVER_PID=$!
sleep 1

# Wait for the server to actually accept connections (up to 5 s).
for _ in 1 2 3 4 5; do
    if curl -sf "http://localhost:$PORT/cxb/" > /dev/null 2>&1; then break; fi
    sleep 1
done

# Copy the smoke runner next to its node_modules so Node's ESM resolver
# can find puppeteer-core via the standard upward-walk lookup. Keeps the
# script in the repo as the source of truth.
cp "$REPO_ROOT/admin_scripts/smoke-test.mjs" "$WORKDIR/smoke-test.mjs"
node "$WORKDIR/smoke-test.mjs" \
    "cxb=$CXB_DIST=http://localhost:$PORT/cxb/=assets" \
    "catalog=$CAT_DIST=http://localhost:$PORT/cat/=assets/js"
