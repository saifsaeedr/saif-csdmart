#!/bin/bash
set -e

# Generate info.json with git metadata (mirrors Python's bundler.py).
# This file is embedded into the binary as a resource and served by
# the "dmart info" subcommand + /info/manifest endpoint.
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

dotnet clean dmart.csproj -q
rm -rf bin obj
dotnet publish dmart.csproj -r linux-x64 -p:PublishAot=true -c Release
