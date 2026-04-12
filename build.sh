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

dotnet clean dmart.csproj -q 2>/dev/null || true
rm -rf bin obj

dotnet publish dmart.csproj -r linux-x64 \
  -p:PublishAot=true \
  -p:StripSymbols=true \
  -c Release

# Clean up dev-only files from publish output
PUBLISH_DIR="bin/Release/net10.0/linux-x64/publish"
rm -f "$PUBLISH_DIR"/*.dbg "$PUBLISH_DIR"/*.pdb \
      "$PUBLISH_DIR"/*.Development.json \
      "$PUBLISH_DIR"/*.staticwebassets* \
      "$PUBLISH_DIR"/*.deps.json

echo ""
echo "Published to $PUBLISH_DIR/"
ls -lh "$PUBLISH_DIR/dmart"
du -sh "$PUBLISH_DIR/"
