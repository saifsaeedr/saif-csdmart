#!/bin/bash
set -e

# Collect git metadata — baked into the binary via InformationalVersion
BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")
COMMIT=$(git rev-parse --short HEAD 2>/dev/null || echo "")
TAG=$(git describe --tags 2>/dev/null || echo "")
VERSION_DATE=$(git show --pretty=format:%ad --date=iso -q 2>/dev/null | head -1 || echo "")
INFORMATIONAL_VERSION="${TAG:-${COMMIT:-0.1.0}} branch=${BRANCH} date=${VERSION_DATE}"
echo "Version: $INFORMATIONAL_VERSION"

# Build UI frontends (cxb + catalog, both embedded into the dmart binary).
# Pre-built dists → skip. Missing dists with a JS toolchain on PATH → build.
# Missing dists with no toolchain → fail with an actionable message (this is
# the el9/alpine RPM container path: host must pre-build via build-ui.sh).
if [ -f cxb/dist/client/index.html ] && [ -f catalog/dist/client/index.html ]; then
    echo "UI frontends already built, skipping"
elif command -v yarn > /dev/null 2>&1 || command -v npm > /dev/null 2>&1; then
    if [ -f cxb/package.json ] || [ -f catalog/package.json ]; then
        echo "=== Building UI frontends ==="
        ./build-ui.sh || { echo "UI build failed"; exit 1; }
    else
        echo "Skipping UI build (no cxb or catalog source found)"
    fi
else
    echo "Error: UI dist missing and no yarn/npm on PATH." >&2
    echo "       Run ./build-ui.sh on the host (which has a JS toolchain)" >&2
    echo "       before invoking this build — dmart's RPM builder containers" >&2
    echo "       don't ship Node.js." >&2
    exit 1
fi

RID="linux-x64"

# AOT publish the single binary (server + CLI client)
dotnet publish dmart.csproj -r "$RID" \
  -p:PublishAot=true \
  -p:StripSymbols=true \
  -p:InformationalVersion="$INFORMATIONAL_VERSION" \
  -c Release

# Clean up dev-only files from publish output
PUBLISH_DIR="bin/Release/net10.0/${RID}/publish"
rm -f "$PUBLISH_DIR"/*.dbg "$PUBLISH_DIR"/*.pdb \
      "$PUBLISH_DIR"/*.Development.json \
      "$PUBLISH_DIR"/*.staticwebassets* \
      "$PUBLISH_DIR"/*.deps.json

# Copy binary to top-level bin/ for easy access
mkdir -p bin
cp "$PUBLISH_DIR/dmart" bin/

echo ""
echo "Published to $PUBLISH_DIR/"
ls -lh "$PUBLISH_DIR/dmart"
du -sh "$PUBLISH_DIR/"
echo ""
echo "Binary copied to bin/:"
ls -lh bin/dmart
