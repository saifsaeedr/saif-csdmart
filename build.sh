#!/bin/bash
set -e

# Collect git metadata — baked into the binary via InformationalVersion.
# `git describe --tags --long` always emits "<tag>-<n>-g<sha>" — even when
# HEAD is exactly on the tag (n=0). Without --long, `git describe` collapses
# to just the tag name on tagged commits and the short SHA disappears from
# `dmart -v` output for release builds.
BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")
# actions/checkout lands on a detached HEAD for tag/release builds, so a raw
# `rev-parse --abbrev-ref` returns the literal string "HEAD". Prefer the
# workflow-supplied GITHUB_REF_NAME when that happens so `dmart -v` shows a
# useful label instead of "HEAD".
if [ "$BRANCH" = "HEAD" ] && [ -n "$GITHUB_REF_NAME" ]; then
  BRANCH="$GITHUB_REF_NAME"
fi
DESCRIBE=$(git describe --tags --always --long 2>/dev/null || echo "0.1.0")
VERSION_DATE=$(git show --pretty=format:%ad --date=iso -q 2>/dev/null | head -1 || echo "")
INFORMATIONAL_VERSION="${DESCRIBE} branch=${BRANCH} date=${VERSION_DATE}"
echo "Version: $INFORMATIONAL_VERSION"

# Build UI frontends (cxb + catalog, both embedded into the dmart binary).
# Each SPA is optional and tracked independently: a SPA "needs building" only
# when its source exists AND its dist output is missing. An absent source is
# fine (the csproj's EmbeddedResource glob simply matches zero files), which
# matches sparse checkouts or forks that ship only one of the two.
needs_build=false
[ -f cxb/package.json ]     && [ ! -f cxb/dist/client/index.html ]     && needs_build=true
[ -f catalog/package.json ] && [ ! -f catalog/dist/client/index.html ] && needs_build=true

if [ "$needs_build" = "false" ]; then
    echo "UI frontends ready (dists present or sources absent), skipping"
elif command -v yarn > /dev/null 2>&1 || command -v npm > /dev/null 2>&1; then
    echo "=== Building UI frontends ==="
    ./build-ui.sh || { echo "UI build failed"; exit 1; }
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
