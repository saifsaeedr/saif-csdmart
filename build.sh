#!/bin/bash
set -e

# Generate info.json with git metadata (mirrors Python's bundler.py).
BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")
COMMIT=$(git rev-parse --short HEAD 2>/dev/null || echo "")
TAG=$(git describe --tags 2>/dev/null || echo "")
VERSION_DATE=$(git show --pretty=format:%ad --date=iso -q 2>/dev/null | head -1 || echo "")

cat > info.json << EOF
{
  "branch": "$BRANCH",
  "version": "$COMMIT",
  "tag": "$TAG",
  "version_date": "$VERSION_DATE",
  "runtime": ".NET $(dotnet --version 2>/dev/null || echo 'unknown')"
}
EOF
echo "Generated info.json: $(cat info.json)"

# Build CXB frontend (embedded into the dmart binary)
if [ -f cxb/package.json ]; then
    echo "=== Building CXB frontend ==="
    ./build-cxb.sh
else
    echo "Skipping CXB build (cxb/package.json not found)"
fi

RID="linux-x64"

# AOT publish the server (includes CXB via EmbeddedResource)
dotnet publish dmart.csproj -r "$RID" \
  -p:PublishAot=true \
  -p:StripSymbols=true \
  -c Release

# Publish the CLI tool as AOT native binary
dotnet publish dmart.Cli/dmart.Cli.csproj -r "$RID" -c Release \
  -p:PublishAot=true \
  -p:StripSymbols=true

# Clean up dev-only files from publish output
PUBLISH_DIR="bin/Release/net10.0/${RID}/publish"
rm -f "$PUBLISH_DIR"/*.dbg "$PUBLISH_DIR"/*.pdb \
      "$PUBLISH_DIR"/*.Development.json \
      "$PUBLISH_DIR"/*.staticwebassets* \
      "$PUBLISH_DIR"/*.deps.json \
      info.json

# Copy binaries to top-level bin/ for easy access
mkdir -p bin
cp "$PUBLISH_DIR/dmart" bin/
cp "dmart.Cli/bin/Release/net10.0/${RID}/publish/dmart-cli" bin/ 2>/dev/null \
  || cp "dmart.Cli/bin/Release/net10.0/dmart-cli" bin/ 2>/dev/null \
  || echo "Warning: dmart-cli binary not found"

echo ""
echo "Published to $PUBLISH_DIR/"
ls -lh "$PUBLISH_DIR/dmart"
du -sh "$PUBLISH_DIR/"
echo ""
echo "Binaries copied to bin/:"
ls -lh bin/dmart bin/dmart-cli 2>/dev/null
